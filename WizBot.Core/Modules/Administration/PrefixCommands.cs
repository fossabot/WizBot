﻿using Discord;
using Discord.Commands;
using System.Threading.Tasks;
using WizBot.Common.Attributes;

namespace WizBot.Modules.Administration
{
    public partial class Administration
    {
        [Group]
        public class PrefixCommands : WizBotSubmodule
        {
            [WizBotCommand, Usage, Description, Aliases]
            [Priority(1)]
            public new async Task Prefix()
            {
                await ReplyConfirmLocalizedAsync("prefix_current", Format.Code(CmdHandler.GetPrefix(ctx.Guild))).ConfigureAwait(false);
                return;
            }

            [WizBotCommand, Usage, Description, Aliases]
            [RequireContext(ContextType.Guild)]
            [UserPerm(GuildPerm.Administrator)]
            [Priority(0)]
            public new async Task Prefix([Leftover]string prefix)
            {
                if (string.IsNullOrWhiteSpace(prefix))
                    return;

                var oldPrefix = base.Prefix;
                var newPrefix = CmdHandler.SetPrefix(ctx.Guild, prefix);

                await ReplyConfirmLocalizedAsync("prefix_new", Format.Code(oldPrefix), Format.Code(newPrefix)).ConfigureAwait(false);
            }

            [WizBotCommand, Usage, Description, Aliases]
            [OwnerOnly]
            public async Task DefPrefix([Leftover]string prefix = null)
            {
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    await ReplyConfirmLocalizedAsync("defprefix_current", CmdHandler.DefaultPrefix).ConfigureAwait(false);
                    return;
                }

                var oldPrefix = CmdHandler.DefaultPrefix;
                var newPrefix = CmdHandler.SetDefaultPrefix(prefix);

                await ReplyConfirmLocalizedAsync("defprefix_new", Format.Code(oldPrefix), Format.Code(newPrefix)).ConfigureAwait(false);
            }
        }
    }
}
