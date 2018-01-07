using System;
using System.Web.Http;
using Microsoft.Bot.Connector;
using System.Net.Http;
using Microsoft.Bot.Builder.Dialogs;
using System.Net;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Reflection;
using Microsoft.Bot.Builder.Luis;
using Microsoft.Bot.Builder.Luis.Models;
using WeatherForecast.LuisActions;

namespace WeatherForecast.Controllers
{
    [BotAuthentication]
    public class MessagesController : ApiController
    {
        public async Task<HttpResponseMessage> Post([FromBody] Activity activity)
        {
            if (activity.Type == ActivityTypes.Message)
            {
                await Conversation.SendAsync(activity, () => new RootDialog());
            }

            return Request.CreateResponse(HttpStatusCode.OK);
        }
    }

    public delegate Task LuisActionHandler(IDialogContext context, object actionResult);
    public delegate Task LuisActionActivityHandler(IDialogContext context, IAwaitable<IMessageActivity> message, object actionResult);

    [Serializable]
    public class RootDialog : LuisDialog<object>
    {
        private readonly LuisActionResolver _actionResolver;

        public RootDialog() : base(new LuisService(new LuisModelAttribute(
            ConfigurationManager.AppSettings["LUIS_ModelId"],
            ConfigurationManager.AppSettings["LUIS_SubscriptionKey"])))
        {
            _actionResolver = new LuisActionResolver(typeof(WeatherForecastAction).Assembly);
        }

        protected override async Task MessageReceived(IDialogContext context, IAwaitable<IMessageActivity> item)
        {
            var message = await item;
            var messageText = await GetLuisQueryTextAsync(context, message);

            var tasks = this.services.Select(s => s.QueryAsync(messageText, context.CancellationToken)).ToArray();
            var results = await Task.WhenAll(tasks);

            var winners = from result in results.Select((value, index) => new { value, index })
                          let resultWinner = this.BestIntentFrom(result.value)
                          where resultWinner != null
                          select new LuisServiceResult(result.value, resultWinner, this.services[result.index]);

            var winner = this.BestResultFrom(winners);

            if (winner == null)
            {
                throw new InvalidOperationException("No winning intent selected from Luis results.");
            }

            var intentName = default(string);
            var luisAction = _actionResolver.ResolveActionFromLuisIntent(winner.Result, out intentName);
            if (luisAction != null)
            {
                await DispatchToLuisActionActivityHandler(context, item, intentName, luisAction);
            }
        }

        [LuisIntent("")]
        [LuisIntent("None")]
        public async Task None(IDialogContext context, object actionResult)
        {
            var noneResult = (LuisResult)actionResult;
            if (noneResult != null)
            {
                var message = $"Sorry, I did not understand '{noneResult.Query}'. Type 'help' if you need assistance.";
                await context.PostAsync(message);
            }

            context.Wait(MessageReceived);
        }

        [LuisIntent("Weather.GetForecast")]
        public async Task WeatherGetForeCastActionHandlerAsync(IDialogContext context, object actionResult)
        {
            IMessageActivity message = null;
            var adaptiveCard = (AdaptiveCards.AdaptiveCard) actionResult;
            if (adaptiveCard == null)
            {
                message = context.MakeMessage();
                message.Text =
                    $"I couldn't find the weather for '{context.Activity.AsMessageActivity().Text}'.  Are you sure that's a real city?";
            }
            else
            {
                message = GetAdaptiveCardMessage(context, adaptiveCard, "Weather Forecast");
            }

            await context.PostAsync(message);
        }

        protected virtual async Task DispatchToLuisActionActivityHandler(IDialogContext context, IAwaitable<IMessageActivity> item, string intentName, ILuisAction luisAction)
        {
            var actionHandlerByIntent = new Dictionary<string, LuisActionActivityHandler>(this.GetActionHandlersByIntent());

            var handler = default(LuisActionActivityHandler);
            if (!actionHandlerByIntent.TryGetValue(intentName, out handler))
            {
                handler = actionHandlerByIntent[string.Empty];
            }

            if (handler != null)
            {
                await handler(context, item, await PerformActionFulfillment(context, item, luisAction));
            }
            else
            {
                throw new Exception($"No default intent handler found.");
            }
        }

        protected virtual IDictionary<string, LuisActionActivityHandler> GetActionHandlersByIntent()
        {
            return EnumerateHandlers(this).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        public static IEnumerable<KeyValuePair<string, LuisActionActivityHandler>> EnumerateHandlers(object dialog)
        {
            var type = dialog.GetType();
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (var method in methods)
            {
                var intents = method.GetCustomAttributes<LuisIntentAttribute>(inherit: true).ToArray();
                LuisActionActivityHandler intentHandler = null;

                try
                {
                    intentHandler = (LuisActionActivityHandler)Delegate.CreateDelegate(typeof(LuisActionActivityHandler), dialog, method, throwOnBindFailure: false);
                }
                catch (ArgumentException)
                {
                    // "Cannot bind to the target method because its signature or security transparency is not compatible with that of the delegate type."
                    // https://github.com/Microsoft/BotBuilder/issues/634
                    // https://github.com/Microsoft/BotBuilder/issues/435
                }

                // fall back for compatibility
                if (intentHandler == null)
                {
                    try
                    {
                        var handler = (LuisActionHandler)Delegate.CreateDelegate(typeof(LuisActionHandler), dialog, method, throwOnBindFailure: false);

                        if (handler != null)
                        {
                            // thunk from new to old delegate type
                            intentHandler = (context, message, result) => handler(context, result);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // "Cannot bind to the target method because its signature or security transparency is not compatible with that of the delegate type."
                        // https://github.com/Microsoft/BotBuilder/issues/634
                        // https://github.com/Microsoft/BotBuilder/issues/435
                    }
                }

                if (intentHandler != null)
                {
                    var intentNames = intents.Select(i => i.IntentName).DefaultIfEmpty(method.Name);

                    foreach (var intentName in intentNames)
                    {
                        var key = string.IsNullOrWhiteSpace(intentName) ? string.Empty : intentName;
                        yield return new KeyValuePair<string, LuisActionActivityHandler>(intentName, intentHandler);
                    }
                }
                else
                {
                    if (intents.Length > 0)
                    {
                        var msg = $"Handler '{method.Name}' signature is not valid for the following intent/s: {string.Join(";", intents.Select(i => i.IntentName))}";
                        throw new InvalidIntentHandlerException(msg, method);
                    }
                }
            }
        }

        protected virtual async Task<object> PerformActionFulfillment(IDialogContext context, IAwaitable<IMessageActivity> item, ILuisAction luisAction)
        {
            return await luisAction.FulfillAsync();
        }

        private static IMessageActivity GetAdaptiveCardMessage(IDialogContext context, AdaptiveCards.AdaptiveCard card,
            string cardName)
        {
            var message = context.MakeMessage();
            if (message.Attachments == null)
                message.Attachments = new List<Attachment>();

            var attachment = new Attachment()
            {
                Content = card,
                ContentType = "application/vnd.microsoft.card.adaptive",
                Name = cardName
            };
            message.Attachments.Add(attachment);
            return message;
        }
    }
}