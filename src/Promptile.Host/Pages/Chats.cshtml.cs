using Microsoft.AspNetCore.Mvc.RazorPages;
using Promptile.Host.Services;

namespace Promptile.Host.Pages;

public class ChatsModel : PageModel
{
    private readonly ConversationStore _store;

    public ChatsModel(ConversationStore store)
    {
        _store = store;
    }

    public List<Conversation> RecentConversations { get; set; } = [];

    public async Task OnGetAsync()
    {
        RecentConversations = (await _store.ListAsync()).Take(30).ToList();
    }
}
