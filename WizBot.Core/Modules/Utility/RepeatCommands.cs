﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using WizBot.Common.Attributes;
using WizBot.Common.TypeReaders;
using WizBot.Core.Common;
using WizBot.Core.Services;
using WizBot.Core.Services.Database.Models;
using WizBot.Extensions;
using WizBot.Modules.Utility.Common;
using WizBot.Modules.Utility.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WizBot.Modules.Utility
{
    public partial class Utility
    {
        [Group]
        public class RepeatCommands : WizBotSubmodule<MessageRepeaterService>
        {
            private readonly DiscordSocketClient _client;
            private readonly DbService _db;

            public RepeatCommands(DiscordSocketClient client, DbService db)
            {
                _client = client;
                _db = db;
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageMessages)]
            public async Task RepeatInvoke(int index)
            {
                if (!_service.RepeaterReady)
                    return;
                index -= 1;
                if (!_service.Repeaters.TryGetValue(ctx.Guild.Id, out var rep))
                {
                    await ReplyErrorLocalizedAsync("repeat_invoke_none").ConfigureAwait(false);
                    return;
                }

                var repList = rep.ToList();

                if (index >= repList.Count)
                {
                    await ReplyErrorLocalizedAsync("index_out_of_range").ConfigureAwait(false);
                    return;
                }
                var repeater = repList[index];
                repeater.Value.Reset();
                await repeater.Value.Trigger().ConfigureAwait(false);

                try { await ctx.Message.AddReactionAsync(new Emoji("🔄")).ConfigureAwait(false); } catch { }
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageMessages)]
            public async Task RepeatRemove(int index)
            {
                if (!_service.RepeaterReady)
                    return;
                if (index < 1)
                    return;
                index -= 1;

                if (!_service.Repeaters.TryGetValue(ctx.Guild.Id, out var rep))
                    return;

                var repeaterList = rep.ToList();

                if (index >= repeaterList.Count)
                {
                    await ReplyErrorLocalizedAsync("index_out_of_range").ConfigureAwait(false);
                    return;
                }

                var repeater = repeaterList[index];
                if (rep.TryRemove(repeater.Value.Repeater.Id, out var runner))
                    runner.Stop();

                using (var uow = _db.GetDbContext())
                {
                    var guildConfig = uow.GuildConfigs.ForId(ctx.Guild.Id, set => set.Include(gc => gc.GuildRepeaters));

                    var item = guildConfig.GuildRepeaters.FirstOrDefault(r => r.Id == repeater.Value.Repeater.Id);
                    if (item != null)
                    {
                        guildConfig.GuildRepeaters.Remove(item);
                        uow._context.Remove(item);
                    }
                    await uow.SaveChangesAsync();
                }
                await ctx.Channel.SendConfirmAsync(GetText("message_repeater"),
                    GetText("repeater_stopped", index + 1) + $"\n\n{repeater.Value}").ConfigureAwait(false);
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageMessages)]
            [WizBotOptions(typeof(Repeater.Options))]
            [Priority(0)]
            public Task Repeat(params string[] options)
                => Repeat(null, options);

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageMessages)]
            [WizBotOptions(typeof(Repeater.Options))]
            [Priority(1)]
            public async Task Repeat(GuildDateTime dt, params string[] options)
            {
                if (!_service.RepeaterReady)
                    return;

                var (opts, _) = OptionsParser.ParseFrom(new Repeater.Options(), options);

                if (string.IsNullOrWhiteSpace(opts.Message) || opts.Interval == 25001)
                    return;

                var toAdd = new Repeater()
                {
                    ChannelId = ctx.Channel.Id,
                    GuildId = ctx.Guild.Id,
                    Interval = TimeSpan.FromMinutes(opts.Interval),
                    Message = opts.Message,
                    NoRedundant = opts.NoRedundant,
                    StartTimeOfDay = dt?.InputTimeUtc.TimeOfDay,
                };

                using (var uow = _db.GetDbContext())
                {
                    var gc = uow.GuildConfigs.ForId(ctx.Guild.Id, set => set.Include(x => x.GuildRepeaters));

                    if (gc.GuildRepeaters.Count >= 5)
                        return;
                    gc.GuildRepeaters.Add(toAdd);

                    await uow.SaveChangesAsync();
                }

                var rep = new RepeatRunner((SocketGuild)ctx.Guild, toAdd, _service);

                _service.Repeaters.AddOrUpdate(ctx.Guild.Id,
                    new ConcurrentDictionary<int, RepeatRunner>(new[] { new KeyValuePair<int, RepeatRunner>(toAdd.Id, rep) }), (key, old) =>
                  {
                      old.TryAdd(rep.Repeater.Id, rep);
                      return old;
                  });

                string secondPart = "";
                if (dt != null)
                {
                    secondPart = GetText("repeater_initial",
                        Format.Bold(rep.InitialInterval.Hours.ToString()),
                        Format.Bold(rep.InitialInterval.Minutes.ToString()));
                }

                await ctx.Channel.SendConfirmAsync(
                    "🔁 " + GetText("repeater",
                        Format.Bold(((IGuildUser)ctx.User).GuildPermissions.MentionEveryone ? rep.Repeater.Message : rep.Repeater.Message.SanitizeMentions()),
                        Format.Bold(rep.Repeater.Interval.Days.ToString()),
                        Format.Bold(rep.Repeater.Interval.Hours.ToString()),
                        Format.Bold(rep.Repeater.Interval.Minutes.ToString())) + " " + secondPart).ConfigureAwait(false);
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.ManageMessages)]
            public async Task RepeatList()
            {
                if (!_service.RepeaterReady)
                    return;
                if (!_service.Repeaters.TryGetValue(ctx.Guild.Id, out var repRunners))
                {
                    await ReplyConfirmLocalizedAsync("repeaters_none").ConfigureAwait(false);
                    return;
                }

                var replist = repRunners.ToList();
                var sb = new StringBuilder();

                for (var i = 0; i < replist.Count; i++)
                {
                    var rep = replist[i];

                    sb.AppendLine($"`{i + 1}.` {rep.Value}");
                }
                var desc = sb.ToString();

                if (string.IsNullOrWhiteSpace(desc))
                    desc = GetText("no_active_repeaters");

                await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                        .WithTitle(GetText("list_of_repeaters"))
                        .WithDescription(desc))
                    .ConfigureAwait(false);
            }
        }
    }
}