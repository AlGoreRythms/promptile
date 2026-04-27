using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Slack;

public class SlackApiClient
{
    private static readonly HttpClient Http = new();

    private readonly string _token;

    public SlackApiClient(string token)
    {
        _token = token;
    }

    public async Task<SlackAuthInfo?> AuthTestAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync("auth.test", [], ct);
        if (!resp.GetProperty("ok").GetBoolean()) return null;
        return new SlackAuthInfo(
            resp.GetProperty("user").GetString() ?? "",
            resp.GetProperty("team").GetString() ?? "");
    }

    public async Task<List<SlackChannel>> ListChannelsAsync(CancellationToken ct = default)
    {
        var channels = new List<SlackChannel>();
        string? cursor = null;

        do
        {
            var args = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["exclude_archived"] = "true",
                ["limit"] = "200",
            };
            if (cursor != null) args["cursor"] = cursor;

            var resp = await GetAsync("conversations.list", args, ct);
            if (!resp.GetProperty("ok").GetBoolean()) break;

            foreach (var ch in resp.GetProperty("channels").EnumerateArray())
            {
                // Only track channels the bot is actually a member of; conversations.history
                // returns an error for channels the bot hasn't joined, so non-member channels
                // would silently produce no data.
                var isMember = ch.TryGetProperty("is_member", out var m) && m.GetBoolean();
                if (!isMember) continue;

                channels.Add(new SlackChannel(
                    ch.GetProperty("id").GetString() ?? "",
                    ch.GetProperty("name").GetString() ?? "",
                    ch.TryGetProperty("is_private", out var priv) && priv.GetBoolean()));
            }

            cursor = null;
            if (resp.TryGetProperty("response_metadata", out var meta)
                && meta.TryGetProperty("next_cursor", out var nc))
            {
                var c = nc.GetString();
                if (!string.IsNullOrEmpty(c)) cursor = c;
            }
        } while (cursor != null);

        return channels;
    }

    public async Task<List<SlackMessage>> GetMessagesAsync(
        string channelId, int limit = 20, string? oldest = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string>
        {
            ["channel"] = channelId,
            ["limit"] = limit.ToString(),
        };
        if (oldest != null) args["oldest"] = oldest;

        var resp = await GetAsync("conversations.history", args, ct);
        if (!resp.GetProperty("ok").GetBoolean()) return [];

        return ParseMessages(resp);
    }

    public async Task<List<SlackMessage>> GetMessagesSinceAsync(
        string channelId, string oldestTs, CancellationToken ct = default)
    {
        var all = new List<SlackMessage>();
        string? cursor = null;

        do
        {
            var args = new Dictionary<string, string>
            {
                ["channel"] = channelId,
                ["oldest"]  = oldestTs,
                ["limit"]   = "200",
            };
            if (cursor != null) args["cursor"] = cursor;

            var resp = await GetAsync("conversations.history", args, ct);
            if (!resp.GetProperty("ok").GetBoolean()) break;

            all.AddRange(ParseMessages(resp));

            cursor = null;
            if (resp.TryGetProperty("response_metadata", out var meta)
                && meta.TryGetProperty("next_cursor", out var nc))
            {
                var c = nc.GetString();
                if (!string.IsNullOrEmpty(c)) cursor = c;
            }
        } while (cursor != null);

        return all;
    }

    private static List<SlackMessage> ParseMessages(JsonElement resp)
    {
        var messages = new List<SlackMessage>();
        foreach (var msg in resp.GetProperty("messages").EnumerateArray())
        {
            var type = msg.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type != "message") continue;

            var subtype = msg.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            if (subtype == "bot_message" || subtype == "channel_join") continue;

            var textField = msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
            var blocksText = msg.TryGetProperty("blocks", out var blocks) ? ExtractBlocksText(blocks) : "";
            var text = blocksText.Length > textField.Length ? blocksText : textField;

            messages.Add(new SlackMessage(
                Ts: msg.GetProperty("ts").GetString() ?? "",
                UserId: msg.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "",
                Text: text,
                ThreadTs: msg.TryGetProperty("thread_ts", out var tts) ? tts.GetString() : null,
                ReplyCount: msg.TryGetProperty("reply_count", out var rc) ? rc.GetInt32() : 0
            ));
        }
        return messages;
    }

    private static string ExtractBlocksText(JsonElement blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "rich_text":
                    if (block.TryGetProperty("elements", out var rtEls))
                        AppendRichTextElements(rtEls, sb);
                    break;
                case "section":
                    if (block.TryGetProperty("text", out var secText)
                        && secText.TryGetProperty("text", out var st))
                        sb.AppendLine(st.GetString());
                    break;
                case "header":
                    if (block.TryGetProperty("text", out var hdrText)
                        && hdrText.TryGetProperty("text", out var ht))
                        sb.AppendLine($"# {ht.GetString()}");
                    break;
            }
        }
        return sb.ToString().Trim();
    }

    private static void AppendRichTextElements(JsonElement elements, StringBuilder sb)
    {
        foreach (var el in elements.EnumerateArray())
        {
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "rich_text_section":
                    if (el.TryGetProperty("elements", out var secEls))
                    {
                        AppendInlineElements(secEls, sb);
                        sb.AppendLine();
                    }
                    break;
                case "rich_text_list":
                    if (el.TryGetProperty("elements", out var listEls))
                    {
                        var style = el.TryGetProperty("style", out var s) ? s.GetString() : "bullet";
                        var i = 1;
                        foreach (var item in listEls.EnumerateArray())
                        {
                            sb.Append(style == "ordered" ? $"{i++}. " : "• ");
                            if (item.TryGetProperty("elements", out var itemEls))
                                AppendInlineElements(itemEls, sb);
                            sb.AppendLine();
                        }
                    }
                    break;
                case "rich_text_preformatted":
                    if (el.TryGetProperty("elements", out var preEls))
                    {
                        sb.AppendLine("```");
                        AppendInlineElements(preEls, sb);
                        sb.AppendLine("```");
                    }
                    break;
                case "rich_text_quote":
                    if (el.TryGetProperty("elements", out var quoteEls))
                    {
                        sb.Append("> ");
                        AppendInlineElements(quoteEls, sb);
                        sb.AppendLine();
                    }
                    break;
            }
        }
    }

    private static void AppendInlineElements(JsonElement elements, StringBuilder sb)
    {
        foreach (var item in elements.EnumerateArray())
        {
            var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;
            switch (itemType)
            {
                case "text":
                    if (item.TryGetProperty("text", out var te)) sb.Append(te.GetString());
                    break;
                case "link":
                    var linkText = item.TryGetProperty("text", out var lt) ? lt.GetString() : null;
                    var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                    sb.Append(linkText ?? url ?? "");
                    break;
                case "user":
                    if (item.TryGetProperty("user_id", out var uid)) sb.Append($"<@{uid.GetString()}>");
                    break;
                case "channel":
                    if (item.TryGetProperty("channel_id", out var cid)) sb.Append($"<#{cid.GetString()}>");
                    break;
                case "emoji":
                    if (item.TryGetProperty("name", out var en)) sb.Append($":{en.GetString()}:");
                    break;
            }
        }
    }

    public async Task<List<SlackMessage>> GetThreadRepliesAsync(
        string channelId, string threadTs, string? oldest = null, CancellationToken ct = default)
    {
        var args = new Dictionary<string, string>
        {
            ["channel"] = channelId,
            ["ts"]      = threadTs,
            ["limit"]   = "100",
        };
        if (oldest != null) args["oldest"] = oldest;

        var resp = await GetAsync("conversations.replies", args, ct);
        if (!resp.GetProperty("ok").GetBoolean()) return [];

        var replies = new List<SlackMessage>();
        var first = true;
        foreach (var msg in resp.GetProperty("messages").EnumerateArray())
        {
            if (first) { first = false; continue; } // first entry is the parent — skip it
            replies.Add(new SlackMessage(
                Ts: msg.GetProperty("ts").GetString() ?? "",
                UserId: msg.TryGetProperty("user", out var u) ? u.GetString() ?? "" : "",
                Text: msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
                ThreadTs: threadTs,
                ReplyCount: 0
            ));
        }
        return replies;
    }

    public async Task<string> GetUserNameAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var resp = await GetAsync("users.info", new() { ["user"] = userId }, ct);
            if (!resp.GetProperty("ok").GetBoolean()) return userId;
            var user = resp.GetProperty("user");
            if (user.TryGetProperty("profile", out var profile)
                && profile.TryGetProperty("display_name", out var dn)
                && !string.IsNullOrEmpty(dn.GetString()))
                return dn.GetString()!;
            if (user.TryGetProperty("real_name", out var rn)) return rn.GetString() ?? userId;
        }
        catch { }
        return userId;
    }

    private async Task<JsonElement> GetAsync(string method, Dictionary<string, string> args, CancellationToken ct)
    {
        var query = string.Join("&", args.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"https://slack.com/api/{method}{(query.Length > 0 ? "?" + query : "")}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var response = await Http.SendAsync(req, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json).RootElement;
    }
}

public record SlackAuthInfo(string User, string Team);
public record SlackChannel(string Id, string Name, bool IsPrivate);
public record SlackMessage(string Ts, string UserId, string Text, string? ThreadTs, int ReplyCount = 0);
