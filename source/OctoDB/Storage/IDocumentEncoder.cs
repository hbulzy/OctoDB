using System;
using System.IO;
using Newtonsoft.Json;

namespace OctoDB.Storage
{
    public interface IDocumentEncoder
    {
        JsonSerializerSettings SerializerSettings { get; }
        
        object Read(Stream stream, Type type, ProvideAttachmentStreamCallback provideAttachment);

        void Write(Stream stream, object document, Type type, ProvideAttachmentStreamCallback provideAttachment);
    }
}