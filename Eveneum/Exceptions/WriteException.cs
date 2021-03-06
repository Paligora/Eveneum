﻿using System;
using System.Net;

namespace Eveneum
{
    [Serializable]
    public class WriteException : EveneumException
    {
        public WriteException(string streamId, double requestCharge, string message, HttpStatusCode statusCode)
            : this(streamId, requestCharge, message, statusCode, null)
        {}

        public WriteException(string streamId, double requestCharge, string message, HttpStatusCode statusCode, Exception inner)
            : base(streamId, requestCharge, message, inner)
        {
            this.StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode
        {
            get { return (HttpStatusCode)this.Data[nameof(StatusCode)]; }
            private set { this.Data[nameof(StatusCode)] = value; }
        }

        protected WriteException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
