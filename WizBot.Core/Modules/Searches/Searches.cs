﻿using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using WizBot.Common;
using WizBot.Common.Attributes;
using WizBot.Common.Replacements;
using WizBot.Core.Modules.Searches.Common;
using WizBot.Core.Services;
using WizBot.Extensions;
using WizBot.Modules.Searches.Common;
using WizBot.Modules.Searches.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Drawing;
using SixLabors.Primitives;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Configuration = AngleSharp.Configuration;

namespace WizBot.Modules.Searches
{
    public partial class Searches : WizBotTopLevelModule<SearchesService>
    {
        private readonly IBotCredentials _creds;
        private readonly IGoogleApiService _google;
        private readonly IHttpClientFactory _httpFactory;
        private static readonly WizBotRandom _rng = new WizBotRandom();

        public Searches(IBotCredentials creds, IGoogleApiService google, IHttpClientFactory factory)
        {
            _creds = creds;
            _google = google;
            _httpFactory = factory;
        }

        //for anonymasen :^)
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Rip([Leftover]IGuildUser usr)
        {
            var av = usr.RealAvatarUrl();
            if (av == null)
                return;
            using (var picStream = await _service.GetRipPictureAsync(usr.Nickname ?? usr.Username, av).ConfigureAwait(false))
            {
                await ctx.Channel.SendFileAsync(
                    picStream,
                    "rip.png",
                    $"Rip {Format.Bold(usr.ToString())} \n\t- " +
                        Format.Italics(ctx.User.ToString()))
                    .ConfigureAwait(false);
            }
        }

        [WizBotCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPerm.ManageMessages)]
        [Priority(1)]
        public async Task Say(ITextChannel channel, [Leftover]string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            var rep = new ReplacementBuilder()
                        .WithDefault(ctx.User, channel, (SocketGuild)ctx.Guild, (DiscordSocketClient)ctx.Client)
                        .Build();

            if (CREmbed.TryParse(message, out var embedData))
            {
                rep.Replace(embedData);
                try
                {
                    await channel.EmbedAsync(embedData.ToEmbed(), embedData.PlainText?.SanitizeMentions() ?? "").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Warn(ex);
                }
            }
            else
            {
                var msg = rep.Replace(message);
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    await channel.SendConfirmAsync(msg).ConfigureAwait(false);
                }
            }
        }

        [WizBotCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        [UserPerm(GuildPerm.ManageMessages)]
        [Priority(0)]
        public Task Say([Leftover]string message) =>
            Say((ITextChannel)ctx.Channel, message);

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Weather([Leftover] string query)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            var embed = new EmbedBuilder();
            var data = await _service.GetWeatherDataAsync(query).ConfigureAwait(false);

            if (data == null)
            {
                embed.WithDescription(GetText("city_not_found"))
                    .WithErrorColor();
            }
            else
            {
                Func<double, double> f = StandardConversions.CelsiusToFahrenheit;

                embed.AddField(fb => fb.WithName("🌍 " + Format.Bold(GetText("location"))).WithValue($"[{data.Name + ", " + data.Sys.Country}](https://openweathermap.org/city/{data.Id})").WithIsInline(true))
                    .AddField(fb => fb.WithName("📏 " + Format.Bold(GetText("latlong"))).WithValue($"{data.Coord.Lat}, {data.Coord.Lon}").WithIsInline(true))
                    .AddField(fb => fb.WithName("☁ " + Format.Bold(GetText("condition"))).WithValue(string.Join(", ", data.Weather.Select(w => w.Main))).WithIsInline(true))
                    .AddField(fb => fb.WithName("😓 " + Format.Bold(GetText("humidity"))).WithValue($"{data.Main.Humidity}%").WithIsInline(true))
                    .AddField(fb => fb.WithName("💨 " + Format.Bold(GetText("wind_speed"))).WithValue(data.Wind.Speed + " m/s").WithIsInline(true))
                    .AddField(fb => fb.WithName("🌡 " + Format.Bold(GetText("temperature"))).WithValue($"{data.Main.Temp:F1}°C / {f(data.Main.Temp):F1}°F").WithIsInline(true))
                    .AddField(fb => fb.WithName("🔆 " + Format.Bold(GetText("min_max"))).WithValue($"{data.Main.TempMin:F1}°C - {data.Main.TempMax:F1}°C\n{f(data.Main.TempMin):F1}°F - {f(data.Main.TempMax):F1}°F").WithIsInline(true))
                    .AddField(fb => fb.WithName("🌄 " + Format.Bold(GetText("sunrise"))).WithValue($"{data.Sys.Sunrise.ToUnixTimestamp():HH:mm} UTC").WithIsInline(true))
                    .AddField(fb => fb.WithName("🌇 " + Format.Bold(GetText("sunset"))).WithValue($"{data.Sys.Sunset.ToUnixTimestamp():HH:mm} UTC").WithIsInline(true))
                    .WithOkColor()
                    .WithFooter(efb => efb.WithText("Powered by openweathermap.org").WithIconUrl($"http://openweathermap.org/img/w/{data.Weather[0].Icon}.png"));
            }
            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        [NoPublicBot]
        public async Task Time([Leftover] string query)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            if (string.IsNullOrWhiteSpace(_creds.GoogleApiKey))
            {
                await ReplyErrorLocalizedAsync("google_api_key_missing").ConfigureAwait(false);
                return;
            }

            var data = await _service.GetTimeDataAsync(query).ConfigureAwait(false);

            await ReplyConfirmLocalizedAsync("time",
                Format.Bold(data.Address),
                Format.Code(data.Time.ToString("HH:mm")),
                data.TimeZoneName).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Youtube([Leftover] string query = null)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            var result = (await _google.GetVideoLinksByKeywordAsync(query, 1).ConfigureAwait(false)).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(result))
            {
                await ReplyErrorLocalizedAsync("no_results").ConfigureAwait(false);
                return;
            }

            await ctx.Channel.SendMessageAsync(result).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Movie([Leftover] string query = null)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            await ctx.Channel.TriggerTypingAsync().ConfigureAwait(false);

            var movie = await _service.GetMovieDataAsync(query).ConfigureAwait(false);
            if (movie == null)
            {
                await ReplyErrorLocalizedAsync("imdb_fail").ConfigureAwait(false);
                return;
            }
            await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                .WithTitle(movie.Title)
                .WithUrl($"http://www.imdb.com/title/{movie.ImdbId}/")
                .WithDescription(movie.Plot.TrimTo(1000))
                .AddField(efb => efb.WithName("Rating").WithValue(movie.ImdbRating).WithIsInline(true))
                .AddField(efb => efb.WithName("Genre").WithValue(movie.Genre).WithIsInline(true))
                .AddField(efb => efb.WithName("Year").WithValue(movie.Year).WithIsInline(true))
                .WithImageUrl(movie.Poster)).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public Task RandomCat() => InternalRandomImage(SearchesService.ImageTag.Cats);

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public Task RandomDog() => InternalRandomImage(SearchesService.ImageTag.Dogs);

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public Task RandomFood() => InternalRandomImage(SearchesService.ImageTag.Food);

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public Task RandomBird() => InternalRandomImage(SearchesService.ImageTag.Birds);

        // done in 3.0
        private Task InternalRandomImage(SearchesService.ImageTag tag)
        {
            var url = _service.GetRandomImageUrl(tag);
            return ctx.Channel.EmbedAsync(new EmbedBuilder()
                .WithOkColor()
                .WithImageUrl(url));
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Image([Leftover] string query = null)
        {
            var oterms = query?.Trim();
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;
            query = WebUtility.UrlEncode(oterms).Replace(' ', '+');
            try
            {
                var res = await _google.GetImageAsync(oterms).ConfigureAwait(false);
                var embed = new EmbedBuilder()
                    .WithOkColor()
                    .WithAuthor(eab => eab.WithName(GetText("image_search_for") + " " + oterms.TrimTo(50))
                        .WithUrl("https://www.google.rs/search?q=" + query + "&source=lnms&tbm=isch")
                        .WithIconUrl("http://i.imgur.com/G46fm8J.png"))
                    .WithDescription(res.Link)
                    .WithImageUrl(res.Link)
                    .WithTitle(ctx.User.ToString());
                await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            }
            catch
            {
                _log.Warn("Falling back to Imgur");

                var fullQueryLink = $"http://imgur.com/search?q={ query }";
                var config = Configuration.Default.WithDefaultLoader();
                using (var document = await BrowsingContext.New(config).OpenAsync(fullQueryLink).ConfigureAwait(false))
                {
                    var elems = document.QuerySelectorAll("a.image-list-link").ToList();

                    if (!elems.Any())
                        return;

                    var img = (elems.ElementAtOrDefault(new WizBotRandom().Next(0, elems.Count))?.Children?.FirstOrDefault() as IHtmlImageElement);

                    if (img?.Source == null)
                        return;

                    var source = img.Source.Replace("b.", ".", StringComparison.InvariantCulture);

                    var embed = new EmbedBuilder()
                        .WithOkColor()
                        .WithAuthor(eab => eab.WithName(GetText("image_search_for") + " " + oterms.TrimTo(50))
                            .WithUrl(fullQueryLink)
                            .WithIconUrl("http://s.imgur.com/images/logo-1200-630.jpg?"))
                        .WithDescription(source)
                        .WithImageUrl(source)
                        .WithTitle(ctx.User.ToString());
                    await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
                }
            }
        }

        [WizBotCommand, Usage, Description, Aliases]
        public async Task Lmgtfy([Leftover] string ffs = null)
        {
            if (!await ValidateQuery(ctx.Channel, ffs).ConfigureAwait(false))
                return;

            await ctx.Channel.SendConfirmAsync("<" + await _google.ShortenUrl($"http://lmgtfy.com/?q={ Uri.EscapeUriString(ffs) }").ConfigureAwait(false) + ">")
                           .ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Shorten([Leftover] string query)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            var shortened = await _google.ShortenUrl(query).ConfigureAwait(false);

            if (shortened == query)
            {
                await ReplyErrorLocalizedAsync("shorten_fail").ConfigureAwait(false);
                return;
            }

            await ctx.Channel.EmbedAsync(new EmbedBuilder().WithColor(WizBot.OkColor)
                .AddField(efb => efb.WithName(GetText("original_url"))
                                    .WithValue($"<{query}>"))
                .AddField(efb => efb.WithName(GetText("short_url"))
                                    .WithValue($"<{shortened}>")))
                .ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Google([Leftover] string query = null)
        {
            var oterms = query?.Trim();
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            query = WebUtility.UrlEncode(oterms).Replace(' ', '+');

            var fullQueryLink = $"https://www.google.ca/search?q={ query }&safe=on&lr=lang_eng&hl=en&ie=utf-8&oe=utf-8";

            using (var msg = new HttpRequestMessage(HttpMethod.Get, fullQueryLink))
            {
                msg.Headers.AddFakeHeaders();
                var config = Configuration.Default.WithDefaultLoader();
                var parser = new HtmlParser();
                var test = "";
                using (var http = _httpFactory.CreateClient())
                using (var response = await http.SendAsync(msg).ConfigureAwait(false))
                using (var document = await parser.ParseDocumentAsync(test = await response.Content.ReadAsStringAsync().ConfigureAwait(false)).ConfigureAwait(false))
                {
                    var elems = document.QuerySelectorAll("div.g");

                    var resultsElem = document.QuerySelectorAll("#resultStats").FirstOrDefault();
                    var totalResults = resultsElem?.TextContent;
                    //var time = resultsElem.Children.FirstOrDefault()?.TextContent
                    //^ this doesn't work for some reason, <nobr> is completely missing in parsed collection
                    if (!elems.Any())
                        return;

                    var results = elems.Select<IElement, GoogleSearchResult?>(elem =>
                    {
                        var aTag = elem.QuerySelector("a") as IHtmlAnchorElement; // <h3> -> <a>
                        var href = aTag?.Href;
                        var name = aTag?.Children.FirstOrDefault()?.TextContent;
                        if (href == null || name == null)
                            return null;

                        var txt = elem.QuerySelectorAll(".st").FirstOrDefault()?.TextContent;

                        if (txt == null)
                            return null;

                        return new GoogleSearchResult(name, href, txt);
                    }).Where(x => x != null).Take(5);

                    var embed = new EmbedBuilder()
                        .WithOkColor()
                        .WithAuthor(eab => eab.WithName(GetText("search_for") + " " + oterms.TrimTo(50))
                            .WithUrl(fullQueryLink)
                            .WithIconUrl("http://i.imgur.com/G46fm8J.png"))
                        .WithTitle(ctx.User.ToString())
                        .WithFooter(efb => efb.WithText(totalResults));

                    var desc = await Task.WhenAll(results.Select(async res =>
                            $"[{Format.Bold(res?.Title)}]({(await _google.ShortenUrl(res?.Link).ConfigureAwait(false))})\n{res?.Text?.TrimTo(400 - res.Value.Title.Length - res.Value.Link.Length)}\n\n"))
                        .ConfigureAwait(false);
                    var descStr = string.Concat(desc);
                    await ctx.Channel.EmbedAsync(embed.WithDescription(descStr)).ConfigureAwait(false);
                }
            }
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task MagicTheGathering([Leftover] string search)
        {
            if (!await ValidateQuery(ctx.Channel, search))
                return;

            await ctx.Channel.TriggerTypingAsync().ConfigureAwait(false);
            var card = await _service.GetMtgCardAsync(search).ConfigureAwait(false);

            if (card == null)
            {
                await ReplyErrorLocalizedAsync("card_not_found").ConfigureAwait(false);
                return;
            }

            var embed = new EmbedBuilder().WithOkColor()
                .WithTitle(card.Name)
                .WithDescription(card.Description)
                .WithImageUrl(card.ImageUrl)
                .AddField(efb => efb.WithName(GetText("store_url")).WithValue(card.StoreUrl).WithIsInline(true))
                .AddField(efb => efb.WithName(GetText("cost")).WithValue(card.ManaCost).WithIsInline(true))
                .AddField(efb => efb.WithName(GetText("types")).WithValue(card.Types).WithIsInline(true));

            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Hearthstone([Leftover] string name)
        {
            var arg = name;
            if (!await ValidateQuery(ctx.Channel, name).ConfigureAwait(false))
                return;

            if (string.IsNullOrWhiteSpace(_creds.MashapeKey))
            {
                await ReplyErrorLocalizedAsync("mashape_api_missing").ConfigureAwait(false);
                return;
            }

            await ctx.Channel.TriggerTypingAsync().ConfigureAwait(false);
            var card = await _service.GetHearthstoneCardDataAsync(name).ConfigureAwait(false);

            if (card == null)
            {
                await ReplyErrorLocalizedAsync("card_not_found").ConfigureAwait(false);
                return;
            }
            var embed = new EmbedBuilder().WithOkColor()
                .WithImageUrl(card.Img);

            if (!string.IsNullOrWhiteSpace(card.Flavor))
                embed.WithDescription(card.Flavor);

            await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task UrbanDict([Leftover] string query = null)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            if (string.IsNullOrWhiteSpace(_creds.MashapeKey))
            {
                await ReplyErrorLocalizedAsync("mashape_api_missing").ConfigureAwait(false);
                return;
            }

            await ctx.Channel.TriggerTypingAsync().ConfigureAwait(false);
            using (var http = _httpFactory.CreateClient())
            {
                var res = await http.GetStringAsync($"http://api.urbandictionary.com/v0/define?term={Uri.EscapeUriString(query)}").ConfigureAwait(false);
                try
                {
                    var items = JsonConvert.DeserializeObject<UrbanResponse>(res).List;
                    if (items.Any())
                    {

                        await ctx.SendPaginatedConfirmAsync(0, (p) =>
                        {
                            var item = items[p];
                            return new EmbedBuilder().WithOkColor()
                                         .WithUrl(item.Permalink)
                                         .WithAuthor(eab => eab.WithIconUrl("http://i.imgur.com/nwERwQE.jpg").WithName(item.Word))
                                         .WithDescription(item.Definition);
                        }, items.Length, 1).ConfigureAwait(false);
                        return;
                    }
                }
                catch
                {
                }
            }
            await ReplyErrorLocalizedAsync("ud_error").ConfigureAwait(false);

        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Define([Leftover] string word)
        {
            if (!await ValidateQuery(ctx.Channel, word).ConfigureAwait(false))
                return;

            using (var http = _httpFactory.CreateClient())
            {
                var res = await http.GetStringAsync("http://api.pearson.com/v2/dictionaries/entries?headword=" + WebUtility.UrlEncode(word.Trim())).ConfigureAwait(false);

                var data = JsonConvert.DeserializeObject<DefineModel>(res);

                var sense = data.Results.FirstOrDefault(x => x.Senses?[0].Definition != null)?.Senses[0];

                if (sense?.Definition == null)
                {
                    await ReplyErrorLocalizedAsync("define_unknown").ConfigureAwait(false);
                    return;
                }

                var definition = sense.Definition.ToString();
                if (!(sense.Definition is string))
                    definition = ((JArray)JToken.Parse(sense.Definition.ToString())).First.ToString();

                var embed = new EmbedBuilder().WithOkColor()
                    .WithTitle(GetText("define") + " " + word)
                    .WithDescription(definition)
                    .WithFooter(efb => efb.WithText(sense.Gramatical_info?.Type));

                if (sense.Examples != null)
                    embed.AddField(efb => efb.WithName(GetText("example")).WithValue(sense.Examples.First().Text));

                await ctx.Channel.EmbedAsync(embed).ConfigureAwait(false);
            }
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Hashtag([Leftover] string query)
        {
            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            if (string.IsNullOrWhiteSpace(_creds.MashapeKey))
            {
                await ReplyErrorLocalizedAsync("mashape_api_missing").ConfigureAwait(false);
                return;
            }

            try
            {
                await ctx.Channel.TriggerTypingAsync().ConfigureAwait(false);
                string res;
                using (var http = _httpFactory.CreateClient())
                {
                    http.DefaultRequestHeaders.Clear();
                    http.DefaultRequestHeaders.Add("X-Mashape-Key", _creds.MashapeKey);
                    res = await http.GetStringAsync($"https://tagdef.p.mashape.com/one.{Uri.EscapeUriString(query)}.json").ConfigureAwait(false);
                }

                var items = JObject.Parse(res);
                var item = items["defs"]["def"];
                //var hashtag = item["hashtag"].ToString();
                var link = item["uri"].ToString();
                var desc = item["text"].ToString();
                await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                                                                 .WithAuthor(eab => eab.WithUrl(link)
                                                                                       .WithIconUrl("http://res.cloudinary.com/urbandictionary/image/upload/a_exif,c_fit,h_200,w_200/v1394975045/b8oszuu3tbq7ebyo7vo1.jpg")
                                                                                       .WithName(query))
                                                                 .WithDescription(desc))
                                                                 .ConfigureAwait(false);
            }
            catch
            {
                await ReplyErrorLocalizedAsync("hashtag_error").ConfigureAwait(false);
            }
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Catfact()
        {
            using (var http = _httpFactory.CreateClient())
            {
                var response = await http.GetStringAsync("https://catfact.ninja/fact").ConfigureAwait(false);
                if (response == null)
                    return;

                var fact = JObject.Parse(response)["fact"].ToString();
                await ctx.Channel.SendConfirmAsync("🐈" + GetText("catfact"), fact).ConfigureAwait(false);
            }
        }

        //done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Revav([Leftover] IGuildUser usr = null)
        {
            if (usr == null)
                usr = (IGuildUser)ctx.User;

            var av = usr.RealAvatarUrl();
            if (av == null)
                return;

            await ctx.Channel.SendConfirmAsync($"https://images.google.com/searchbyimage?image_url={av}").ConfigureAwait(false);
        }

        //done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Revimg([Leftover] string imageLink = null)
        {
            imageLink = imageLink?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(imageLink))
                return;
            await ctx.Channel.SendConfirmAsync($"https://images.google.com/searchbyimage?image_url={imageLink}").ConfigureAwait(false);
        }

        [WizBotCommand, Usage, Description, Aliases]
        public Task Safebooru([Leftover] string tag = null)
            => InternalDapiCommand(ctx.Message, tag, DapiSearchType.Safebooru);

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Wiki([Leftover] string query = null)
        {
            query = query?.Trim();

            if (!await ValidateQuery(ctx.Channel, query).ConfigureAwait(false))
                return;

            using (var http = _httpFactory.CreateClient())
            {
                var result = await http.GetStringAsync("https://en.wikipedia.org//w/api.php?action=query&format=json&prop=info&redirects=1&formatversion=2&inprop=url&titles=" + Uri.EscapeDataString(query)).ConfigureAwait(false);
                var data = JsonConvert.DeserializeObject<WikipediaApiModel>(result);
                if (data.Query.Pages[0].Missing || string.IsNullOrWhiteSpace(data.Query.Pages[0].FullUrl))
                    await ReplyErrorLocalizedAsync("wiki_page_not_found").ConfigureAwait(false);
                else
                    await ctx.Channel.SendMessageAsync(data.Query.Pages[0].FullUrl).ConfigureAwait(false);
            }
        }

        [WizBotCommand, Usage, Description, Aliases]
        public async Task Color(params Rgba32[] colors)
        {
            if (!colors.Any())
                return;

            var colorObjects = colors.Take(10)
                .ToArray();

            using (var img = new Image<Rgba32>(colorObjects.Length * 50, 50))
            {
                for (int i = 0; i < colorObjects.Length; i++)
                {
                    var x = i * 50;
                    img.Mutate(m => m.FillPolygon(colorObjects[i], new PointF[] {
                        new PointF(x, 0),
                        new PointF(x + 50, 0),
                        new PointF(x + 50, 50),
                        new PointF(x, 50)
                    }));
                }
                using (var ms = img.ToStream())
                {
                    await ctx.Channel.SendFileAsync(ms, $"colors.png").ConfigureAwait(false);
                }
            }
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Avatar([Leftover] IGuildUser usr = null)
        {
            if (usr == null)
                usr = (IGuildUser)ctx.User;

            var avatarUrl = usr.RealAvatarUrl();

            if (avatarUrl == null)
            {
                await ReplyErrorLocalizedAsync("avatar_none", usr.ToString()).ConfigureAwait(false);
                return;
            }

            var shortenedAvatarUrl = await _google.ShortenUrl(avatarUrl).ConfigureAwait(false);
            await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                .AddField(efb => efb.WithName("Username").WithValue(usr.ToString()).WithIsInline(false))
                .AddField(efb => efb.WithName("Avatar Url").WithValue(shortenedAvatarUrl).WithIsInline(false))
                .WithThumbnailUrl(avatarUrl.ToString())
                .WithImageUrl(avatarUrl.ToString()), ctx.User.Mention).ConfigureAwait(false);
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        public async Task Wikia(string target, [Leftover] string query)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(query))
            {
                await ReplyErrorLocalizedAsync("wikia_input_error").ConfigureAwait(false);
                return;
            }
            await ctx.Channel.TriggerTypingAsync().ConfigureAwait(false);
            using (var http = _httpFactory.CreateClient())
            {
                http.DefaultRequestHeaders.Clear();
                try
                {
                    var res = await http.GetStringAsync($"http://www.{Uri.EscapeUriString(target)}.wikia.com/api/v1/Search/List?query={Uri.EscapeUriString(query)}&limit=25&minArticleQuality=10&batch=1&namespaces=0%2C14").ConfigureAwait(false);
                    var items = JObject.Parse(res);
                    var found = items["items"][0];
                    var response = $@"`{GetText("title")}` {found["title"]}
`{GetText("quality")}` {found["quality"]}
`{GetText("url")}:` {await _google.ShortenUrl(found["url"].ToString()).ConfigureAwait(false)}";
                    await ctx.Channel.SendMessageAsync(response).ConfigureAwait(false);
                }
                catch
                {
                    await ReplyErrorLocalizedAsync("wikia_error").ConfigureAwait(false);
                }
            }
        }

        /* [WizBotCommand, Usage, Description, Aliases]
        public async Task Nya([Remainder] string category = "neko")
        {
            string[] cat = { "wallpaper", "ngif", "meow", "tickle", "feed", "gecg", "kemonomimi", "gasm", "poke", "slap", "avatar", "lizard", "waifu", "pat", "kiss", "neko", "cuddle", "fox_girl", "hug", "baka", "smug" };
            if (string.IsNullOrWhiteSpace(category))
                return;

            try
            {
                JToken nyatitle;
                JToken nyaimg;
                using (var http = _httpFactory.CreateClient())
                {
                    nyatitle = JObject.Parse(await http.GetStringAsync($"https://nekos.life/api/v2/cat").ConfigureAwait(false));
                    nyaimg = JObject.Parse(await http.GetStringAsync($"https://nekos.life/api/v2/img/{category}").ConfigureAwait(false));
                }
                if (cat.Contains(category))
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                        .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                            .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                            .WithName($"Nekos Life - SFW Database {nyatitle["cat"]}"))
                        .WithImageUrl($"{nyaimg["url"]}")).ConfigureAwait(false);
                else
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithErrorColor()
                    .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                        .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                        .WithName($"Nekos Life - Invalid SFW Category"))
                    .WithDescription("Seems the category you was looking for could not be found. Please use the category listed below.")
                    .AddField(fb => fb.WithName("SFW Categories").WithValue("`wallpaper`,`ngif`,`meow`,`tickle`,`feed`,`gecg`,`kemonomimi`,`gasm`,`poke`,`slap`,`avatar`,`lizard`,`waifu`,`pat`,`kiss`,`neko`,`cuddle`,`fox_girl`,`hug`,`baka`,`smug`").WithIsInline(false))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ctx.Channel.SendErrorAsync(ex.Message).ConfigureAwait(false);
            }
        } */

        [WizBotCommand, Usage, Description, Aliases]
        public async Task Nya(string format = "img", [Remainder] string category = "neko")
        {
            // List if category to pull an image from.
            string[] img_cat = { "kitsune", "keta_avatar", "no_tag_avatar", "holo_avatar", "neko_avatars_avatar", "lizard", "cat", "gecg", "smug", "holo", "wallpaper", "kiminonawa", "shinobu", "neko", "waifu" };

            string[] gif_cat = { "tickle", "poke", "kiss", "slap", "cuddle", "hug", "pat", "smug", "baka", "feed", "neko" };

            // Check to see if the command is calling for a normal image or a gif.
            string[] img_format = { "img", "gif" };

            if (string.IsNullOrWhiteSpace(category))
                return;

            if (string.IsNullOrWhiteSpace(format))
                return;

            try
            {
                JToken nekotitle;
                JToken nekoimg;
                using (var http = _httpFactory.CreateClient())
                {
                    nekotitle = JObject.Parse(await http.GetStringAsync($"https://api.nekos.dev/api/v3/text/cat_emote/").ConfigureAwait(false));
                    nekoimg = JObject.Parse(await http.GetStringAsync($"https://api.nekos.dev/api/v3/images/sfw/{format}/{category}/").ConfigureAwait(false));
                }
                if (img_format.Contains("img") && img_cat.Contains(category))
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                        .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                            .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                            .WithName($"Nekos Life - SFW IMG Database {nekotitle["data"]["response"]["text"]}"))
                        .WithImageUrl($"{nekoimg["data"]["response"]["url"]}")).ConfigureAwait(false);
                else if (img_format.Contains("gif") && gif_cat.Contains(category))
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                        .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                            .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                            .WithName($"Nekos Life - SFW GIF Database {nekotitle["data"]["response"]["text"]}"))
                        .WithImageUrl($"{nekoimg["data"]["response"]["url"]}")).ConfigureAwait(false);
                else if (img_format.Contains("img") && gif_cat.Contains(category))
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithErrorColor()
                    .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                        .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                        .WithName($"Nekos Life - Invalid SFW IMG Category"))
                    .WithDescription("Seems the image category you was looking for could not be found. Please use the categories listed below.")
                    .AddField(fb => fb.WithName("SFW IMG Categories").WithValue("`kitsune`, `keta_avatar`, `no_tag_avatar`, `holo_avatar`, `neko_avatars_avatar`, `lizard`, `cat`, `gecg`, `smug`, `holo`, `wallpaper`, `kiminonawa`, `shinobu`, `neko`, `waifu`").WithIsInline(false))).ConfigureAwait(false);
                else if (img_format.Contains("gif") && img_cat.Contains(category))
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithErrorColor()
                    .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                        .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                        .WithName($"Nekos Life - Invalid SFW GIF Category"))
                    .WithDescription("Seems the gif category you was looking for could not be found. Please use the categories listed below.")
                    .AddField(fb => fb.WithName("SFW GIF Categories").WithValue("`tickle`, `poke`, `kiss`, `slap`, `cuddle`, `hug`, `pat`, `smug`, `baka`, `feed`, `neko`").WithIsInline(false))).ConfigureAwait(false);
                else
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithErrorColor()
                    .WithAuthor(eab => eab.WithUrl("http://nekos.life/")
                        .WithIconUrl("https://i.imgur.com/a36AMkG.png")
                        .WithName($"Nekos Life - Invalid SFW Image Type or Category"))
                    .WithDescription("Seems the image type or category you was looking for could not be found. Please use the image type or categories listed below.")
                    .AddField(fb => fb.WithName("SFW IMG Types").WithValue("`img`, `gif`").WithIsInline(false))
                    .AddField(fb => fb.WithName("SFW IMG Categories").WithValue("`kitsune`, `keta_avatar`, `no_tag_avatar`, `holo_avatar`, `neko_avatars_avatar`, `lizard`, `cat`, `gecg`, `smug`, `holo`, `wallpaper`, `kiminonawa`, `shinobu`, `neko`, `waifu`").WithIsInline(false))
                    .AddField(fb => fb.WithName("SFW GIF Categories").WithValue("`tickle`, `poke`, `kiss`, `slap`, `cuddle`, `hug`, `pat`, `smug`, `baka`, `feed`, `neko`").WithIsInline(false))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ctx.Channel.SendErrorAsync(ex.Message).ConfigureAwait(false);
            }
        }

        // Waifu Gen Command

        [WizBotCommand, Usage, Description, Aliases]
        public async Task GWaifu()
        {
            try 
            {
                using (var http = _httpFactory.CreateClient())
                {
                    var waifutxt = await http.GetStringAsync($"https://www.thiswaifudoesnotexist.net/snippet-{new WizBotRandom().Next(0, 100000)}.txt").ConfigureAwait(false);
                    await ctx.Channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                        .WithAuthor(eab => eab.WithUrl("https://www.thiswaifudoesnotexist.net")
                            .WithName($"This Waifu Does Not Exist"))
                        .WithThumbnailUrl($"https://www.thiswaifudoesnotexist.net/example-{new WizBotRandom().Next(0, 100000)}.jpg")
                        .WithDescription($"{waifutxt}".TrimTo(1000))).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await ctx.Channel.SendErrorAsync(ex.Message).ConfigureAwait(false);
            }
        }

        // done in 3.0
        [WizBotCommand, Usage, Description, Aliases]
        [RequireContext(ContextType.Guild)]
        public async Task Bible(string book, string chapterAndVerse)
        {
            var obj = new BibleVerses();
            try
            {
                using (var http = _httpFactory.CreateClient())
                {
                    var res = await http
                        .GetStringAsync("https://bible-api.com/" + book + " " + chapterAndVerse).ConfigureAwait(false);

                    obj = JsonConvert.DeserializeObject<BibleVerses>(res);
                }
            }
            catch
            {
            }
            if (obj.Error != null || obj.Verses == null || obj.Verses.Length == 0)
                await ctx.Channel.SendErrorAsync(obj.Error ?? "No verse found.").ConfigureAwait(false);
            else
            {
                var v = obj.Verses[0];
                await ctx.Channel.EmbedAsync(new EmbedBuilder()
                    .WithOkColor()
                    .WithTitle($"{v.BookName} {v.Chapter}:{v.Verse}")
                    .WithDescription(v.Text)).ConfigureAwait(false);
            }
        }

        public async Task InternalDapiCommand(IUserMessage umsg, string tag, DapiSearchType type)
        {
            var channel = umsg.Channel;

            tag = tag?.Trim() ?? "";

            var imgObj = await _service.DapiSearch(tag, type, ctx.Guild?.Id).ConfigureAwait(false);

            if (imgObj == null)
                await channel.SendErrorAsync(umsg.Author.Mention + " " + GetText("no_results")).ConfigureAwait(false);
            else
                await channel.EmbedAsync(new EmbedBuilder().WithOkColor()
                    .WithDescription($"{umsg.Author.Mention} [{tag ?? "url"}]({imgObj.FileUrl})")
                    .WithImageUrl(imgObj.FileUrl)
                    .WithFooter(efb => efb.WithText(type.ToString()))).ConfigureAwait(false);
        }

        public async Task<bool> ValidateQuery(IMessageChannel ch, string query)
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            await ErrorLocalizedAsync("specify_search_params").ConfigureAwait(false);
            return false;
        }
    }
}
