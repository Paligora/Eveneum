﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Newtonsoft.Json.Linq;
using Eveneum.Documents;
using System.Threading;
using Eveneum.Advanced;
using Newtonsoft.Json;

namespace Eveneum
{
    public class EventStore : IEventStore, IAdvancedEventStore
    {
        public readonly CosmosClient Client;
        public readonly Database Database;
        public readonly Container Collection;
        public readonly string Partition;
        public readonly PartitionKey? PartitionKey;

        public readonly JsonSerializer JsonSerializer;

        public DeleteMode DeleteMode { get; set; } = DeleteMode.SoftDelete;

        private readonly TypeCache TypeCache = new TypeCache();

        public EventStore(CosmosClient client, string database, string collection, string partition = null, JsonSerializer jsonSerializer = null)
        {
            this.Client = client ?? throw new ArgumentNullException(nameof(client)); 
            this.Database = this.Client.GetDatabase(database ?? throw new ArgumentNullException(nameof(database)));
            this.Collection = this.Database.GetContainer(collection ?? throw new ArgumentNullException(nameof(collection)));
            this.Partition = string.IsNullOrEmpty(partition) ? null : partition;
            this.PartitionKey = string.IsNullOrEmpty(partition) ? (PartitionKey?)null : new PartitionKey(partition); 

            this.JsonSerializer = jsonSerializer ?? JsonSerializer.CreateDefault();
        }

        public async Task<Stream?> ReadStream(string streamId, CancellationToken cancellationToken = default)
        {
            if (streamId == null)
                throw new ArgumentNullException(nameof(streamId));

            var sql = $"SELECT * FROM x WHERE x.{nameof(EveneumDocument.StreamId)} = '{streamId}' ORDER BY x.{nameof(EveneumDocument.SortOrder)} DESC";
            var query = this.Collection.GetItemQueryIterator<EveneumDocument>(sql, requestOptions: new QueryRequestOptions { PartitionKey = this.PartitionKey, MaxItemCount = -1 });

            var documents = new List<EveneumDocument>();
            var finishLoading = false;

            while (query.HasMoreResults)
            {
                var page = await query.ReadNextAsync(cancellationToken);

                foreach (var eveneumDoc in page)
                {
                    if (eveneumDoc.DocumentType == DocumentType.Header && eveneumDoc.Deleted)
                        throw new StreamNotFoundException(streamId);

                    if (eveneumDoc.Deleted)
                        continue;

                    documents.Add(eveneumDoc);

                    if (eveneumDoc.DocumentType == DocumentType.Snapshot)
                    {
                        finishLoading = true;
                        break;
                    }
                }

                if (finishLoading)
                    break;
            }

            if (documents.Count == 0)
                return null;

            var headerDocument = documents.First();
            var events = documents.Where(x => x.DocumentType == DocumentType.Event).Select(this.DeserializeEvent).Reverse().ToArray();
            var snapshot = documents.Where(x => x.DocumentType == DocumentType.Snapshot).Select(this.DeserializeSnapshot).Cast<Snapshot?>().FirstOrDefault();

            object metadata = null;

            if (!string.IsNullOrEmpty(headerDocument.MetadataType))
                metadata = headerDocument.Metadata.ToObject(this.TypeCache.Resolve(headerDocument.MetadataType), this.JsonSerializer);

            return new Stream(streamId, headerDocument.Version, metadata, events, snapshot);
        }

        public async Task WriteToStream(string streamId, EventData[] events, ulong? expectedVersion = null, object metadata = null, CancellationToken cancellationToken = default)
        {
            EveneumDocument header;

            // Existing stream
            if (expectedVersion.HasValue)
            {
                header = await this.ReadHeader(streamId, cancellationToken);

                if (header.Version != expectedVersion)
                    throw new OptimisticConcurrencyException(streamId, expectedVersion.Value, header.Version);
            }
            else
            {
                header = new EveneumDocument(DocumentType.Header)
                {
                    Partition = this.Partition,
                    StreamId = streamId
                };
            }

            header.Version += (ulong)events.Length;

            if (metadata != null)
            {
                header.Metadata = JToken.FromObject(metadata, this.JsonSerializer);
                header.MetadataType = metadata.GetType().AssemblyQualifiedName;
            }

            if (!expectedVersion.HasValue)
            {
                try
                {
                    await this.Collection.CreateItemAsync(header, this.PartitionKey, cancellationToken: cancellationToken);
                }
                catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    throw new StreamAlreadyExistsException(streamId);
                }
            }
            else
            {
                await this.Collection.ReplaceItemAsync(header, header.Id, this.PartitionKey, new ItemRequestOptions { IfMatchEtag = header.ETag }, cancellationToken);
            }

            var eventDocuments = (events ?? Enumerable.Empty<EventData>()).Select(@event => this.Serialize(@event, streamId));

            foreach (var eventDocument in eventDocuments)
                await this.Collection.CreateItemAsync(eventDocument, this.PartitionKey, cancellationToken: cancellationToken);
        }

        public async Task DeleteStream(string streamId, ulong expectedVersion, CancellationToken cancellationToken = default)
        {
            var header = new EveneumDocument(DocumentType.Header)
            {
                Partition = this.Partition,
                StreamId = streamId,
                Version = expectedVersion
            };

            var existingHeader = await this.ReadHeader(streamId, cancellationToken);

            if (existingHeader.Deleted)
                throw new StreamNotFoundException(streamId);

            if (existingHeader.Version != expectedVersion)
                throw new OptimisticConcurrencyException(streamId, expectedVersion, existingHeader.Version);

            string etag = existingHeader.ETag;

            var query = this.Collection.GetItemLinqQueryable<EveneumDocument>(allowSynchronousQueryExecution: true, requestOptions: new QueryRequestOptions { PartitionKey = this.PartitionKey, MaxItemCount = -1 })
                .Where(x => x.StreamId == streamId)
                .ToFeedIterator();

            do
            {
                var page = await query.ReadNextAsync(cancellationToken);

                foreach (var document in page)
                {
                    if (this.DeleteMode == DeleteMode.SoftDelete)
                    {
                        document.Deleted = true;
                        await this.Collection.UpsertItemAsync(document, this.PartitionKey, cancellationToken: cancellationToken);
                    }
                    else
                        await this.Collection.DeleteItemAsync<EveneumDocument>(document.Id, this.PartitionKey.Value, cancellationToken: cancellationToken);
                }
            } while (query.HasMoreResults);
        }

        public async Task CreateSnapshot(string streamId, ulong version, object snapshot, object metadata = null, bool deleteOlderSnapshots = false, CancellationToken cancellationToken = default)
        {
            var header = await this.ReadHeader(streamId, cancellationToken);

            if (header.Version < version)
                throw new OptimisticConcurrencyException(streamId, version, header.Version);

            var document = this.Serialize(snapshot, metadata, version, streamId);

            await this.Collection.UpsertItemAsync(document, this.PartitionKey, cancellationToken: cancellationToken);

            if (deleteOlderSnapshots)
                await this.DeleteSnapshots(streamId, version, cancellationToken);
        }

        public async Task DeleteSnapshots(string streamId, ulong olderThanVersion, CancellationToken cancellationToken = default)
        {
            await this.ReadHeader(streamId, cancellationToken);

            var query = this.Collection.GetItemLinqQueryable<EveneumDocument>(requestOptions: new QueryRequestOptions { PartitionKey = this.PartitionKey, MaxItemCount = -1 })
                .Where(x => x.StreamId == streamId)
                .Where(x => x.DocumentType == DocumentType.Snapshot)
                .Where(x => x.Version < olderThanVersion)
                .ToFeedIterator();

            do
            {
                var page = await query.ReadNextAsync(cancellationToken);

                foreach (var document in page)
                {
                    if (this.DeleteMode == DeleteMode.SoftDelete)
                    {
                        document.Deleted = true;
                        await this.Collection.UpsertItemAsync(document, this.PartitionKey, cancellationToken: cancellationToken);
                    }
                    else
                        await this.Collection.DeleteItemAsync<EveneumDocument>(document.Id, this.PartitionKey.Value, cancellationToken: cancellationToken);
                }
            } while (query.HasMoreResults);
        }

        public Task LoadAllEvents(Func<IReadOnlyCollection<EventData>, Task> callback, CancellationToken cancellationToken = default) =>
            this.LoadEvents($"SELECT * FROM c WHERE c.{nameof(EveneumDocument.DocumentType)} = '{nameof(DocumentType.Event)}'", callback, cancellationToken);

        public async Task LoadEvents(string sql, Func<IReadOnlyCollection<EventData>, Task> callback, CancellationToken cancellationToken = default)
        {
            var query = this.Collection.GetItemQueryIterator<EveneumDocument>(sql, requestOptions: new QueryRequestOptions { PartitionKey = this.PartitionKey, MaxItemCount = -1 });

            do
            {
                var page = await query.ReadNextAsync(cancellationToken);

                await callback(page.Where(x => x.DocumentType == DocumentType.Event).Where(x => !x.Deleted).Select(DeserializeEvent).ToList());
            }
            while (query.HasMoreResults);
        }

        private async Task<EveneumDocument> ReadHeader(string streamId, CancellationToken cancellationToken = default)
        {
            try
            {
                return await this.Collection.ReadItemAsync<EveneumDocument>(streamId, this.PartitionKey.Value, cancellationToken: cancellationToken);
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new StreamNotFoundException(streamId);
            }
        }

        private EveneumDocument Serialize(EventData @event, string streamId)
        {
            var document = new EveneumDocument(DocumentType.Event)
            {
                Partition = this.Partition,
                StreamId = streamId,
                Version = @event.Version,
                BodyType = @event.Body.GetType().AssemblyQualifiedName,
                Body = JToken.FromObject(@event.Body, this.JsonSerializer)
            };

            if (@event.Metadata != null)
            {
                document.MetadataType = @event.Metadata.GetType().AssemblyQualifiedName;
                document.Metadata = JToken.FromObject(@event.Metadata, this.JsonSerializer);
            }

            return document;
        }

        private EveneumDocument Serialize(object snapshot, object metadata, ulong version, string streamId)
        {
            var document = new EveneumDocument(DocumentType.Snapshot)
            {
                Partition = this.Partition,
                StreamId = streamId,
                Version = version,
                BodyType = snapshot.GetType().AssemblyQualifiedName,
                Body = JToken.FromObject(snapshot, this.JsonSerializer)
            };

            if (metadata != null)
            {
                document.Metadata = JToken.FromObject(metadata, this.JsonSerializer);
                document.MetadataType = metadata.GetType().AssemblyQualifiedName;
            }

            return document;
        }

        private EventData DeserializeEvent(EveneumDocument document)
        {
            object metadata = DeserializeObject(document.MetadataType, document.Metadata);
            object body = DeserializeObject(document.BodyType, document.Body);

            return new EventData(document.StreamId, body, metadata, document.Version);
        }
        
        private object DeserializeObject(string typeName, JToken data)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            var type = this.TypeCache.Resolve(typeName);
            if (type == null)
                throw new TypeNotFoundException(typeName);

            try
            {
                return data.ToObject(type, this.JsonSerializer);
            }
            catch (Exception exc)
            {
                throw new DeserializationException(typeName, data.ToString(), exc);
            }
        }

        private Snapshot DeserializeSnapshot(EveneumDocument document)
        {
            object metadata = null;

            if (!string.IsNullOrEmpty(document.MetadataType))
                metadata = document.Metadata.ToObject(this.TypeCache.Resolve(document.MetadataType), this.JsonSerializer);

            return new Snapshot(document.Body.ToObject(this.TypeCache.Resolve(document.BodyType), this.JsonSerializer), metadata, document.Version);
        }
    }
}
