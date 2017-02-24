﻿using Discord;
using Discord.Commands;
using WizBot.Attributes;
using WizBot.Extensions;
using NLog;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WizBot.Modules.Games
{
    public partial class Games
    {
        [Group]
        public class TicTacToeCommands : WizBotSubmodule
        {
            //channelId/game
            private static readonly Dictionary<ulong, TicTacToe> _games = new Dictionary<ulong, TicTacToe>();

            private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            public async Task TicTacToe()
            {
                var channel = (ITextChannel)Context.Channel;

                await _sem.WaitAsync(1000);
                try
                {
                    TicTacToe game;
                    if (_games.TryGetValue(channel.Id, out game))
                    {
                        var _ = Task.Run(async () =>
                        {
                            await game.Start((IGuildUser)Context.User);
                        });
                        return;
                    }
                    game = new TicTacToe(channel, (IGuildUser)Context.User);
                    _games.Add(channel.Id, game);
                    await ReplyConfirmLocalized("ttt_created").ConfigureAwait(false);

                    game.OnEnded += (g) =>
                    {
                        _games.Remove(channel.Id);
                    };
                }
                finally
                {
                    _sem.Release();
                }
            }
        }

        public class TicTacToe
        {
            enum Phase
            {
                Starting,
                Started,
                Ended
            }

            private readonly ITextChannel _channel;
            private readonly Logger _log;
            private readonly IGuildUser[] _users;
            private readonly int?[,] _state;
            private Phase _phase;
            private int _curUserIndex;
            private readonly SemaphoreSlim _moveLock;

            private IGuildUser _winner;

            private readonly string[] _numbers = { ":one:", ":two:", ":three:", ":four:", ":five:", ":six:", ":seven:", ":eight:", ":nine:" };

            public Action<TicTacToe> OnEnded;

            private IUserMessage _previousMessage;
            private Timer _timeoutTimer;

            public TicTacToe(ITextChannel channel, IGuildUser firstUser)
            {
                _channel = channel;
                _users = new[] { firstUser, null };
                _state = new int?[,] {
                    { null, null, null },
                    { null, null, null },
                    { null, null, null },
                };

                _log = LogManager.GetCurrentClassLogger();
                _phase = Phase.Starting;
                _moveLock = new SemaphoreSlim(1, 1);
            }

            private string GetText(string key, params object[] replacements) =>
                WizBotTopLevelModule.GetTextStatic(key,
                    WizBot.Localization.GetCultureInfo(_channel.GuildId),
                    typeof(Games).Name.ToLowerInvariant(),
                    replacements);

            public string GetState()
            {
                var sb = new StringBuilder();
                for (var i = 0; i < _state.GetLength(0); i++)
                {
                    for (var j = 0; j < _state.GetLength(1); j++)
                    {
                        sb.Append(_state[i, j] == null ? _numbers[i * 3 + j] : GetIcon(_state[i, j]));
                        if (j < _state.GetLength(1) - 1)
                            sb.Append("┃");
                    }
                    if (i < _state.GetLength(0) - 1)
                        sb.AppendLine("\n──────────");
                }

                return sb.ToString();
            }

            public EmbedBuilder GetEmbed(string title = null)
            {
                var embed = new EmbedBuilder()
                    .WithOkColor()
                    .WithDescription(Environment.NewLine + GetState())
                    .WithAuthor(eab => eab.WithName(GetText("vs", _users[0], _users[1])));

                if (!string.IsNullOrWhiteSpace(title))
                    embed.WithTitle(title);

                if (_winner == null)
                {
                    if (_phase == Phase.Ended)
                        embed.WithFooter(efb => efb.WithText(GetText("ttt_no_moves")));
                    else
                        embed.WithFooter(efb => efb.WithText(GetText("ttt_users_move", _users[_curUserIndex])));
                }
                else
                    embed.WithFooter(efb => efb.WithText(GetText("ttt_has_won", _winner)));

                return embed;
            }

            private static string GetIcon(int? val)
            {
                switch (val)
                {
                    case 0:
                        return "❌";
                    case 1:
                        return "⭕";
                    case 2:
                        return "❎";
                    case 3:
                        return "🅾";
                    default:
                        return "⬛";
                }
            }

            public async Task Start(IGuildUser user)
            {
                if (_phase == Phase.Started || _phase == Phase.Ended)
                {
                    await _channel.SendErrorAsync(user.Mention + GetText("ttt_already_running")).ConfigureAwait(false);
                    return;
                }
                else if (_users[0] == user)
                {
                    await _channel.SendErrorAsync(user.Mention + GetText("ttt_against_yourself")).ConfigureAwait(false);
                    return;
                }

                _users[1] = user;

                _phase = Phase.Started;

                _timeoutTimer = new Timer(async (_) =>
                {
                    await _moveLock.WaitAsync();
                    try
                    {
                        if (_phase == Phase.Ended)
                            return;

                        _phase = Phase.Ended;
                        if (_users[1] != null)
                        {
                            _winner = _users[_curUserIndex ^= 1];
                            var del = _previousMessage?.DeleteAsync();
                            try
                            {
                                await _channel.EmbedAsync(GetEmbed(GetText("ttt_time_expired"))).ConfigureAwait(false);
                                if (del != null)
                                    await del.ConfigureAwait(false);
                            }
                            catch { }
                        }

                        OnEnded?.Invoke(this);
                    }
                    catch { }
                    finally
                    {
                        _moveLock.Release();
                    }
                }, null, 15000, Timeout.Infinite);

                WizBot.Client.MessageReceived += Client_MessageReceived;


                _previousMessage = await _channel.EmbedAsync(GetEmbed(GetText("game_started"))).ConfigureAwait(false);
            }

            private bool IsDraw()
            {
                for (var i = 0; i < 3; i++)
                {
                    for (var j = 0; j < 3; j++)
                    {
                        if (_state[i, j] == null)
                            return false;
                    }
                }
                return true;
            }

            private Task Client_MessageReceived(Discord.WebSocket.SocketMessage msg)
            {
                var _ = Task.Run(async () =>
                {
                    await _moveLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var curUser = _users[_curUserIndex];
                        if (_phase == Phase.Ended || msg.Author?.Id != curUser.Id)
                            return;

                        int index;
                        if (int.TryParse(msg.Content, out index) &&
                            --index >= 0 &&
                            index <= 9 &&
                            _state[index / 3, index % 3] == null)
                        {
                            _state[index / 3, index % 3] = _curUserIndex;

                            // i'm lazy
                            if (_state[index / 3, 0] == _state[index / 3, 1] && _state[index / 3, 1] == _state[index / 3, 2])
                            {
                                _state[index / 3, 0] = _curUserIndex + 2;
                                _state[index / 3, 1] = _curUserIndex + 2;
                                _state[index / 3, 2] = _curUserIndex + 2;

                                _phase = Phase.Ended;
                            }
                            else if (_state[0, index % 3] == _state[1, index % 3] && _state[1, index % 3] == _state[2, index % 3])
                            {
                                _state[0, index % 3] = _curUserIndex + 2;
                                _state[1, index % 3] = _curUserIndex + 2;
                                _state[2, index % 3] = _curUserIndex + 2;

                                _phase = Phase.Ended;
                            }
                            else if (_curUserIndex == _state[0, 0] && _state[0, 0] == _state[1, 1] && _state[1, 1] == _state[2, 2])
                            {
                                _state[0, 0] = _curUserIndex + 2;
                                _state[1, 1] = _curUserIndex + 2;
                                _state[2, 2] = _curUserIndex + 2;

                                _phase = Phase.Ended;
                            }
                            else if (_curUserIndex == _state[0, 2] && _state[0, 2] == _state[1, 1] && _state[1, 1] == _state[2, 0])
                            {
                                _state[0, 2] = _curUserIndex + 2;
                                _state[1, 1] = _curUserIndex + 2;
                                _state[2, 0] = _curUserIndex + 2;

                                _phase = Phase.Ended;
                            }
                            var reason = "";

                            if (_phase == Phase.Ended) // if user won, stop receiving moves
                            {
                                reason = GetText("ttt_matched_three");
                                _winner = _users[_curUserIndex];
                                WizBot.Client.MessageReceived -= Client_MessageReceived;
                                OnEnded?.Invoke(this);
                            }
                            else if (IsDraw())
                            {
                                reason = GetText("ttt_a_draw");
                                _phase = Phase.Ended;
                                WizBot.Client.MessageReceived -= Client_MessageReceived;
                                OnEnded?.Invoke(this);
                            }

                            var sendstate = Task.Run(async () =>
                            {
                                var del1 = msg.DeleteAsync();
                                var del2 = _previousMessage?.DeleteAsync();
                                try { _previousMessage = await _channel.EmbedAsync(GetEmbed(reason)); } catch { }
                                try { await del1; } catch { }
                                try { if (del2 != null) await del2; } catch { }
                            });
                            _curUserIndex ^= 1;

                            _timeoutTimer.Change(15000, Timeout.Infinite);
                        }
                    }
                    finally
                    {
                        _moveLock.Release();
                    }
                });

                return Task.CompletedTask;
            }
        }
    }
}