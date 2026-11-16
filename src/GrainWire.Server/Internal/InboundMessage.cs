namespace GrainWire.Server.Internal;

internal readonly record struct InboundMessage(ClientTopic Topic, byte[] Payload);
