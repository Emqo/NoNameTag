namespace Emqo.NoNameTag.Services
{
    public interface IChatMessageSender
    {
        bool Send(ChatMessageDispatch dispatch);
    }
}
