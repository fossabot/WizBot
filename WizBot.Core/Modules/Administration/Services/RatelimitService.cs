using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using WizBot.Common.ModuleBehaviors;
using WizBot.Extensions;
using WizBot.Modules.Administration.Common;
using WizBot.Core.Services;
using WizBot.Core.Services.Database.Models;
using NLog;
using Microsoft.EntityFrameworkCore;

namespace WizBot.Modules.Administration.Services
{
    public class SlowmodeService : IEarlyBlocker, INService
    {
        public ConcurrentDictionary<ulong, Ratelimiter> RatelimitingChannels = new ConcurrentDictionary<ulong, Ratelimiter>();
        public ConcurrentDictionary<ulong, HashSet<ulong>> IgnoredRoles = new ConcurrentDictionary<ulong, HashSet<ulong>>();
        public ConcurrentDictionary<ulong, HashSet<ulong>> IgnoredUsers = new ConcurrentDictionary<ulong, HashSet<ulong>>();
        private readonly DbService _db;
        private readonly Logger _log;
        private readonly DiscordSocketClient _client;

        public SlowmodeService(DiscordSocketClient client, WizBot bot, DbService db)
        {
            _log = LogManager.GetCurrentClassLogger();
            _client = client;

            IgnoredRoles = new ConcurrentDictionary<ulong, HashSet<ulong>>(
                bot.AllGuildConfigs.ToDictionary(x => x.GuildId,
                                 x => new HashSet<ulong>(x.SlowmodeIgnoredRoles.Select(y => y.RoleId))));

            IgnoredUsers = new ConcurrentDictionary<ulong, HashSet<ulong>>(
                bot.AllGuildConfigs.ToDictionary(x => x.GuildId,
                                 x => new HashSet<ulong>(x.SlowmodeIgnoredUsers.Select(y => y.UserId))));

            _db = db;
        }

        public bool StopSlowmode(ulong id)
        {
            return RatelimitingChannels.TryRemove(id, out var x);
        }

        public async Task<bool> TryBlockEarly(IGuild g, IUserMessage usrMsg)
        {
            var guild = g as SocketGuild;
            if (guild == null)
                return false;
            try
            {
                var channel = usrMsg?.Channel as SocketTextChannel;

                if (channel == null || usrMsg == null || usrMsg.IsAuthor(_client))
                    return false;
                if (guild.GetUser(usrMsg.Author.Id).GuildPermissions.ManageMessages)
                    return false;
                if (!RatelimitingChannels.TryGetValue(channel.Id, out Ratelimiter limiter))
                    return false;

                if (CheckUserRatelimit(limiter, channel.Guild.Id, usrMsg.Author.Id, usrMsg.Author as SocketGuildUser))
                {
                    await usrMsg.DeleteAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Warn(ex);

            }
            return false;
        }

        private bool CheckUserRatelimit(Ratelimiter rl, ulong guildId, ulong userId, SocketGuildUser optUser)
        {
            if ((IgnoredUsers.TryGetValue(guildId, out HashSet<ulong> ignoreUsers) && ignoreUsers.Contains(userId)) ||
                   (optUser != null && IgnoredRoles.TryGetValue(guildId, out HashSet<ulong> ignoreRoles) && optUser.Roles.Any(x => ignoreRoles.Contains(x.Id))))
                return false;

            var msgCount = rl.Users.AddOrUpdate(userId, 1, (key, old) => ++old);

            if (msgCount > rl.MaxMessages)
            {
                var test = rl.Users.AddOrUpdate(userId, 0, (key, old) => --old);
                _log.Info("Not allowed: {0}", test);
                return true;
            }

            var _ = Task.Run(async () =>
            {
                await Task.Delay(rl.PerSeconds * 1000);
                var newVal = rl.Users.AddOrUpdate(userId, 0, (key, old) => --old);
                _log.Info("Decreased: {0}", newVal);
            });
            return false;
        }

        public bool ToggleWhitelistUser(ulong guildId, ulong userId)
        {
            var siu = new SlowmodeIgnoredUser
            {
                UserId = userId
            };

            HashSet<SlowmodeIgnoredUser> usrs;
            bool removed;
            using (var uow = _db.UnitOfWork)
            {
                usrs = uow.GuildConfigs.For(guildId, set => set.Include(x => x.SlowmodeIgnoredUsers))
                    .SlowmodeIgnoredUsers;

                if (!(removed = usrs.Remove(siu)))
                    usrs.Add(siu);

                uow.Complete();
            }

            IgnoredUsers.AddOrUpdate(guildId,
                new HashSet<ulong>(usrs.Select(x => x.UserId)),
                (key, old) => new HashSet<ulong>(usrs.Select(x => x.UserId)));

            return !removed;
        }

        public bool ToggleWhitelistRole(ulong guildId, ulong roleId)
        {
            var sir = new SlowmodeIgnoredRole
            {
                RoleId = roleId
            };

            HashSet<SlowmodeIgnoredRole> roles;
            bool removed;
            using (var uow = _db.UnitOfWork)
            {
                roles = uow.GuildConfigs.For(guildId, set => set.Include(x => x.SlowmodeIgnoredRoles))
                    .SlowmodeIgnoredRoles;

                if (!(removed = roles.Remove(sir)))
                    roles.Add(sir);

                uow.Complete();
            }

            IgnoredRoles.AddOrUpdate(guildId,
                new HashSet<ulong>(roles.Select(x => x.RoleId)),
                (key, old) => new HashSet<ulong>(roles.Select(x => x.RoleId)));

            return !removed;
        }

        public bool StartSlowmode(ulong id, uint msgCount, int perSec)
        {
            var rl = new Ratelimiter
            {
                MaxMessages = msgCount,
                PerSeconds = perSec,
            };

            return RatelimitingChannels.TryAdd(id, rl);
        }
    }
}