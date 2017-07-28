﻿using Discord.Commands;
using WizBot.Extensions;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using WizBot.Common.Attributes;
using WizBot.Modules.Games.Common.Hangman;

namespace WizBot.Modules.Games
{
    public partial class Games
    {
        [Group]
        public class HangmanCommands : WizBotSubmodule
        {
            private readonly DiscordSocketClient _client;

            public HangmanCommands(DiscordSocketClient client)
            {
                _client = client;
            }

            //channelId, game
            public static ConcurrentDictionary<ulong, HangmanGame> HangmanGames { get; } = new ConcurrentDictionary<ulong, HangmanGame>();
            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task Hangmanlist()
            {
                await Context.Channel.SendConfirmAsync(Format.Code(GetText("hangman_types", Prefix)) + "\n" + string.Join(", ", HangmanTermPool.data.Keys));
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task Hangman([Remainder]string type = "All")
            {
                var hm = new HangmanGame(_client, Context.Channel, type);

                if (!HangmanGames.TryAdd(Context.Channel.Id, hm))
                {
                    await ReplyErrorLocalized("hangman_running").ConfigureAwait(false);
                    return;
                }

                hm.OnEnded += g =>
                {
                    HangmanGames.TryRemove(g.GameChannel.Id, out _);
                };
                try
                {
                    hm.Start();
                }
                catch (Exception ex)
                {
                    try { await Context.Channel.SendErrorAsync(GetText("hangman_start_errored") + " " + ex.Message).ConfigureAwait(false); } catch { }
                    if(HangmanGames.TryRemove(Context.Channel.Id, out var removed))
                        removed.Dispose();
                    return;
                }

                await Context.Channel.SendConfirmAsync(GetText("hangman_game_started"), hm.ScrambledWord + "\n" + hm.GetHangman());
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task HangmanStop()
            {
                if (HangmanGames.TryRemove(Context.Channel.Id, out var removed))
                {
                    removed.Dispose();
                    await ReplyConfirmLocalized("hangman_stopped").ConfigureAwait(false);
                }
            }
        }
    }
}