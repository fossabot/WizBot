﻿using System.Collections.Concurrent;
using Discord;
using WizBot.Common.Collections;
using WizBot.Services.Database.Models;

namespace WizBot.Modules.Administration.Common
{
    public enum ProtectionType
    {
        Raiding,
        Spamming,
    }

    public class AntiRaidStats
    {
        public AntiRaidSetting AntiRaidSettings { get; set; }
        public int UsersCount { get; set; }
        public ConcurrentHashSet<IGuildUser> RaidUsers { get; set; } = new ConcurrentHashSet<IGuildUser>();
    }

    public class AntiSpamStats
    {
        public AntiSpamSetting AntiSpamSettings { get; set; }
        public ConcurrentDictionary<ulong, UserSpamStats> UserStats { get; set; }
            = new ConcurrentDictionary<ulong, UserSpamStats>();
    }
}