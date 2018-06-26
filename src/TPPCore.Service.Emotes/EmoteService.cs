﻿using Microsoft.AspNetCore.Routing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TPPCore.Service.Common;

namespace TPPCore.Service.Emotes
{
    public class EmoteService : IServiceAsync
    {
        private ServiceContext context;
        private EmoteHandler emoteHandler;
        private CancellationTokenSource token = new CancellationTokenSource();
        string fileLocation;

        public void Initialize(ServiceContext context)
        {
            this.context = context;
            string Cachelocation = context.ConfigReader.GetCheckedValue<string>("emote", "cache_location");
            if (!Directory.Exists(Cachelocation))
                Directory.CreateDirectory(Cachelocation);
            fileLocation = Cachelocation + "emotes.json";
            if (!File.Exists(fileLocation))
                File.Create(fileLocation);
            emoteHandler = new EmoteHandler(context, fileLocation);

            context.RestfulServer.UseRoute((RouteBuilder routeBuilder) =>
            {
                routeBuilder
                    .MapGet("emote/fromid/{id}", emoteHandler.GetEmoteFromId)
                    .MapGet("emote/fromcode/{code}", emoteHandler.GetEmoteFromCode)
                    .MapGet("emote/codetoid/{code}", emoteHandler.EmoteCodeToId)
                    .MapGet("emote/idtocode/{id}", emoteHandler.EmoteIdToCode)
                    .MapGet("emote/idtourl/{id}/{scale}", emoteHandler.EmoteIdToUrl)
                    .MapGet("emote/idtourl/{id}", emoteHandler.EmoteIdToUrlNoScale)
                    .MapGet("emote/findin/{text}", emoteHandler.FindEmotes)
                    ;
            });
        }

        public void Run()
        {
            RunAsync().Wait();
        }

        public async Task RunAsync()
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await emoteHandler.GetEmotes(token.Token);
                    await Task.Delay(1800000, token.Token);
                } catch
                {
                    break;
                }
            }
        }

        public void Shutdown()
        {
            token.Cancel();
        }
    }
}
