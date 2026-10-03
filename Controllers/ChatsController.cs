using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using User.Data;
using User.Entities;
using User.Services;
namespace User.Controllers;
[ApiController, Route("api/chats")]
public class ChatsController(MongoDbContext db, ProfilePolicy profiles, ChatCommands commands, MongoTransactions transactions) : ControllerBase
{
    [HttpPost("create-or-get")] public async Task<object> Create(CreateChatDto dto) => await commands.ChatDto(await commands.Create(Input.Actor(User), dto.ParticipantIds, dto.IsGroup, dto.Name));
    [HttpGet("{id}")] public async Task<object> Get(string id) => await commands.ChatDto(await commands.Member(Input.Actor(User), id));
    [HttpGet("user/{userId}")]
    public async Task<object> List(string userId, int limit = 50, int skip = 0)
    {
        Input.Owner(User, userId); Input.Page(limit, skip); var user = await profiles.Find(userId);
        var chats = await db.Chats.Find(x => x.Participants.Contains(user.Id)).SortByDescending(x => x.LastActivity).Skip(skip).Limit(limit).ToListAsync(); return await commands.ChatDtos(chats);
    }
    [HttpGet("{chatId}/messages")]
    public async Task<object> Messages(string chatId, int page = 1, int pageSize = 50)
    {
        await commands.Member(Input.Actor(User), chatId); Input.Page(pageSize, checked((page - 1) * pageSize));
        var messages = await db.Messages.Find(x => x.ChatId == chatId).SortByDescending(x => x.SentAt).ThenByDescending(x => x.Id).Skip((page - 1) * pageSize).Limit(pageSize).ToListAsync();
        var senderIds = messages.Select(x => x.SenderId).Distinct().ToList(); var users = await db.Users.Find(x => senderIds.Contains(x.Id)).ToListAsync();
        return messages.Select(m => ChatCommands.MessageDto(m, users.FirstOrDefault(x => x.Id == m.SenderId) ?? new Models.User { UserName = "Deleted account" }));
    }
    [HttpPost("{chatId}/messages")] public async Task<object> Send(string chatId, SendMessageDto dto) { if (dto.Type != "Text") throw new ApiProblem(400, "Only text messages are supported."); return await commands.Send(Input.Actor(User), chatId, dto.Content, dto.ClientMessageId, dto.ReplyToId); }
    [HttpPost("messages/{messageId}/read")] public async Task<object> Read(string messageId) { await commands.Read(Input.Actor(User), messageId); return new { message = "Receipt saved" }; }
    [HttpPost("{chatId}/mark-all-read")] public async Task<object> ReadAll(string chatId) { await commands.ReadAll(Input.Actor(User), chatId); return new { message = "Receipts saved" }; }
    [HttpGet("unread-counts")]
    public async Task<object> Counts()
    {
        var actor = Input.Actor(User); var user = await profiles.Find(actor); var ids = await db.Chats.Find(x => x.Participants.Contains(user.Id)).Limit(100).Project(x => x.Id).ToListAsync();
        var unread = await db.Messages.Aggregate().Match(x => ids.Contains(x.ChatId) && x.SenderId != user.Id && !x.ReadBy.Any(r => r.UserId == actor)).Group(x => x.ChatId, g => new { ChatId = g.Key, Count = g.Count() }).ToListAsync(); return unread.ToDictionary(x => x.ChatId, x => x.Count);
    }
    [HttpGet("{chatId}/unread-count")] public async Task<object> Count(string chatId) { var actor = Input.Actor(User); await commands.Member(actor, chatId); var user = await profiles.Find(actor); return await db.Messages.CountDocumentsAsync(x => x.ChatId == chatId && x.SenderId != user.Id && !x.ReadBy.Any(r => r.UserId == actor)); }
    [HttpDelete("{chatId}")] public async Task<object> Delete(string chatId) { await commands.Member(Input.Actor(User), chatId); await transactions.Run(async (session, ct) => { await db.Messages.DeleteManyAsync(session, x => x.ChatId == chatId, cancellationToken: ct); await db.Chats.DeleteOneAsync(session, x => x.Id == chatId, cancellationToken: ct); return true; }, HttpContext.RequestAborted); return new { message = "Chat deleted" }; }
}
public class CreateChatDto { public List<string> ParticipantIds { get; set; } = []; public bool IsGroup { get; set; } public string? Name { get; set; } }
public class SendMessageDto { public string Content { get; set; } = ""; public string Type { get; set; } = "Text"; public string? ClientMessageId { get; set; } public string? ReplyToId { get; set; } }
