﻿using Discord;
using Discord.Commands;
using Microsoft.EntityFrameworkCore;
using WizBot.Common.Attributes;
using WizBot.Common.TypeReaders;
using WizBot.Core.Services;
using WizBot.Core.Services.Database.Models;
using WizBot.Modules.Permissions.Services;
using System.Linq;
using System.Threading.Tasks;

namespace WizBot.Modules.Permissions
{
    public partial class Permissions
    {
        [Group]
        public class BlacklistCommands : WizBotSubmodule<BlacklistService>
        {
            private readonly DbService _db;
            private readonly IBotCredentials _creds;

            public BlacklistCommands(DbService db, IBotCredentials creds)
            {
                _db = db;
                _creds = creds;
            }

            [WizBotCommand, Usage, Description, Aliases]
            [AdminOnly]
            public Task UserBlacklist(AddRemove action, ulong id)
                => Blacklist(action, id, BlacklistType.User);

            [WizBotCommand, Usage, Description, Aliases]
            [AdminOnly]
            public Task UserBlacklist(AddRemove action, IUser usr)
                => Blacklist(action, usr.Id, BlacklistType.User);

            [WizBotCommand, Usage, Description, Aliases]
            [AdminOnly]
            public Task ChannelBlacklist(AddRemove action, ulong id)
                => Blacklist(action, id, BlacklistType.Channel);

            [WizBotCommand, Usage, Description, Aliases]
            [AdminOnly]
            public Task ServerBlacklist(AddRemove action, ulong id)
                => Blacklist(action, id, BlacklistType.Server);

            [WizBotCommand, Usage, Description, Aliases]
            [AdminOnly]
            public Task ServerBlacklist(AddRemove action, IGuild guild)
                => Blacklist(action, guild.Id, BlacklistType.Server);

            private async Task Blacklist(AddRemove action, ulong id, BlacklistType type)
            {
                if (action == AddRemove.Add && _creds.OwnerIds.Contains(id))
                    return;

                using (var uow = _db.GetDbContext())
                {
                    if (action == AddRemove.Add)
                    {
                        var item = new BlacklistItem { ItemId = id, Type = type };
                        uow.BotConfig.GetOrCreate().Blacklist.Add(item);
                    }
                    else
                    {
                        var objs = uow.BotConfig
                            .GetOrCreate(set => set.Include(x => x.Blacklist))
                            .Blacklist
                            .Where(bi => bi.ItemId == id && bi.Type == type);

                        if (objs.Any())
                            uow._context.Set<BlacklistItem>().RemoveRange(objs);
                    }
                    await uow.SaveChangesAsync();
                }

                if (action == AddRemove.Add)
                    await ReplyConfirmLocalizedAsync("blacklisted", Format.Code(type.ToString()), Format.Code(id.ToString())).ConfigureAwait(false);
                else
                    await ReplyConfirmLocalizedAsync("unblacklisted", Format.Code(type.ToString()), Format.Code(id.ToString())).ConfigureAwait(false);
            }
        }
    }
}
