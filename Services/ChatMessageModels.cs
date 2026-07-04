namespace Emqo.NoNameTag.Services
{
    public enum ChatMessageMode
    {
        Global,
        Local,
        Group,
        Say
    }

    public struct ChatMessagePosition
    {
        public ChatMessagePosition(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public float DistanceSquaredTo(ChatMessagePosition other)
        {
            var x = X - other.X;
            var y = Y - other.Y;
            var z = Z - other.Z;
            return x * x + y * y + z * z;
        }
    }

    public sealed class ChatMessageParticipant
    {
        public ulong SteamId { get; set; }
        public string DisplayName { get; set; }
        public ulong GroupId { get; set; }
        public bool CanReceiveGroupChat { get; set; }
        public ChatMessagePosition Position { get; set; }
    }

    public sealed class ChatMessageRequest
    {
        public ChatMessageParticipant Sender { get; set; }
        public System.Collections.Generic.IReadOnlyList<ChatMessageParticipant> Recipients { get; set; }
        public string Message { get; set; }
        public ChatMessageMode ChatMode { get; set; }
        public bool IsCanceled { get; set; }
    }

    public sealed class ChatMessageDispatch
    {
        public ChatMessageParticipant Sender { get; set; }
        public ChatMessageParticipant Recipient { get; set; }
        public string Message { get; set; }
        public string AvatarUrl { get; set; }
        public ChatMessageMode ChatMode { get; set; }
    }
}
