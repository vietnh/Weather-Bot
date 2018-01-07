using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs.Internals;
using Microsoft.Bot.Builder.Luis;
using Microsoft.Bot.Builder.Luis.Models;
using Newtonsoft.Json.Linq;
using WeatherForecast.Attributes;
using WeatherForecast.Constants;

namespace WeatherForecast.LuisActions
{
    [Serializable]
    public class LuisActionResolver
    {
        private readonly IDictionary<string, Type> _luisActions;

        public LuisActionResolver(params Assembly[] lookupAssemblies)
        {
            _luisActions = new Dictionary<string, Type>();

            if (lookupAssemblies == null)
            {
                throw new ArgumentNullException(nameof(lookupAssemblies));
            }

            foreach (var lookupAssembly in lookupAssemblies)
            {
                foreach (var info in lookupAssembly.GetTypes().Select(t => new { Type = t, Attribs = t.GetCustomAttributes<LuisActionBindingAttribute>(true) }).Where(o => o.Attribs.Any()))
                {
                    foreach (var intentAttrib in info.Attribs)
                    {
                        _luisActions.Add(intentAttrib.IntentName, info.Type);
                    }
                }
            }
        }

        public static bool AssignValue(ILuisAction action, string paramName, object paramValue)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (string.IsNullOrWhiteSpace(paramName))
            {
                throw new ArgumentNullException(nameof(paramName));
            }

            if (paramValue == null)
            {
                throw new ArgumentNullException(nameof(paramValue));
            }

            return AssignValue(action, action.GetType().GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance), paramValue);
        }

        public static async Task<QueryValueResult> QueryValueFromLuisAsync(
            ILuisService service,
            ILuisAction action,
            string paramName,
            object paramValue,
            CancellationToken token,
            Func<PropertyInfo, IEnumerable<EntityRecommendation>, EntityRecommendation> entityExtractor = null)
        {
            var originalValue = paramValue;

            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (string.IsNullOrWhiteSpace(paramName))
            {
                throw new ArgumentNullException(nameof(paramName));
            }

            if (paramValue == null)
            {
                throw new ArgumentNullException(nameof(paramValue));
            }

            var result = await service.QueryAsync(paramValue.ToString(), token);
            var queryIntent = result.Intents.FirstOrDefault();
            if (queryIntent != null)
                if (!Intents.None.Equals(queryIntent.Intent, StringComparison.InvariantCultureIgnoreCase))
                {
                    string newIntentName;
                    var newAction =
                        new LuisActionResolver(action.GetType().Assembly).ResolveActionFromLuisIntent(result,
                            out newIntentName);
                    if (newAction != null && !newAction.GetType().Equals(action.GetType()))
                    {
                        return new QueryValueResult(false)
                        {
                            NewAction = newAction,
                            NewIntent = newIntentName
                        };
                    }
                }

            var properties = new List<PropertyInfo> { action.GetType().GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance) };
            if (!AssignEntitiesToMembers(action, properties, result.Entities, entityExtractor))
            {
                return new QueryValueResult(AssignValue(action, properties.First(), originalValue));
            }

            return new QueryValueResult(true);
        }

        public static LuisActionBindingAttribute GetActionDefinition(ILuisAction action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return action.GetType().GetCustomAttributes<LuisActionBindingAttribute>(true).FirstOrDefault();
        }

        public ILuisAction ResolveActionFromLuisIntent(
            LuisResult luisResult,
            out string intentName,
            out IList<EntityRecommendation> intentEntities,
            Func<PropertyInfo, IEnumerable<EntityRecommendation>, EntityRecommendation> entityExtractor = null)
        {
            intentEntities = default(IList<EntityRecommendation>);

            if (luisResult == null)
            {
                throw new ArgumentNullException(nameof(luisResult));
            }

            // Has Intent?
            intentName = (luisResult.TopScoringIntent ?? luisResult.Intents?.MaxBy(i => i.Score ?? 0d)).Intent;
            if (string.IsNullOrWhiteSpace(intentName))
            {
                return null;
            }

            // Set out intent entities reference
            intentEntities = luisResult.Entities;

            // Get Actions that map to this intent
            Type actionType;
            if (!_luisActions.TryGetValue(intentName, out actionType))
            {
                return null;
            }

            // Get the action instance and check if it implements ILuisAction
            var luisAction = Activator.CreateInstance(actionType) as ILuisAction;
            if (luisAction == null)
            {
                return null;
            }

            // Try complete parameters from entities
            var properties = luisAction.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            AssignEntitiesToMembers(luisAction, properties, luisResult.Entities, entityExtractor);

            return luisAction;
        }

        internal ILuisAction ResolveActionFromLuisIntent(LuisResult luisResult, out string intentName)
        {
            if (luisResult == null)
            {
                throw new ArgumentNullException(nameof(luisResult));
            }

            IList<EntityRecommendation> unassigned;

            return ResolveActionFromLuisIntent(luisResult, out intentName, out unassigned);
        }

        private static bool AssignValue(ILuisAction action, PropertyInfo property, object paramValue)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (property == null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            if (paramValue == null)
            {
                throw new ArgumentNullException(nameof(paramValue));
            }

            if (property.CanWrite)
            {
                // nullable support
                var type = property.PropertyType;
                type = Nullable.GetUnderlyingType(type) ?? type;

                try
                {
                    object value;

                    // handle LUIS JObjects
                    paramValue = SanitizeInputValue(type, paramValue);

                    if (type.IsArray)
                    {
                        value = BuildArrayOfValues(action, property, type.GetElementType(), paramValue);
                    }
                    else if (type.IsEnum)
                    {
                        value = Enum.Parse(type, paramValue.ToString());
                    }
                    else
                    {
                        value = Convert.ChangeType(paramValue, type);
                    }

                    property.SetValue(action, value);

                    return true;
                }
                catch (FormatException)
                {
                    // Handle invalid values (Eg. Try Parse '2017' as a Date will fail)
                }
            }

            return false;
        }

        private static Array BuildArrayOfValues(ILuisAction action, PropertyInfo property, Type elementType, object paramValue)
        {
            var objects = paramValue as IEnumerable<object>;
            var values = objects ?? paramValue.ToString().Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).ToList();

            if (values.Any())
            {
                var idx = 0;
                var result = Array.CreateInstance(elementType, values.Count());
                foreach (var value in values)
                {
                    result.SetValue(elementType.IsEnum ? Enum.Parse(elementType, value.ToString()) : Convert.ChangeType(value, elementType), idx++);
                }

                return result;
            }
            else
            {
                return null;
            }
        }

        private static object SanitizeInputValue(Type targetType, object value)
        {
            object result = value;

            // handle case where input is JArray returned from LUIS
            var arrayOfValues = value as JArray;
            if (arrayOfValues != null)
            {
                if (targetType.IsArray)
                {
                    result = arrayOfValues.AsEnumerable<object>();
                }
                else
                {
                    if (arrayOfValues.Count > 1)
                    {
                        throw new FormatException("Cannot assign multiple values to single field");
                    }

                    result = arrayOfValues[0];
                }
            }

            return result;
        }

        private static bool AssignEntitiesToMembers(
            ILuisAction action,
            IEnumerable<PropertyInfo> properties,
            IEnumerable<EntityRecommendation> entities,
            Func<PropertyInfo, IEnumerable<EntityRecommendation>, EntityRecommendation> entityExtractor = null)
        {
            var result = true;

            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (properties == null)
            {
                throw new ArgumentNullException(nameof(properties));
            }

            if (entities == null)
            {
                throw new ArgumentNullException(nameof(entities));
            }

            if (!entities.Any())
            {
                return !result;
            }

            // Cross match entities to copy resolution values for custom entities from pairs
            foreach (var group in entities.GroupBy(e => e.Entity))
            {
                if (group.Count() > 1)
                {
                    var entityToUpdate = group.FirstOrDefault(e => !BuiltInTypes.IsBuiltInType(e.Type));
                    var entityWithValue = group.FirstOrDefault(e => e.Resolution != null);
                    if (entityToUpdate != null && entityWithValue != null)
                    {
                        entityToUpdate.Resolution = entityWithValue.Resolution;
                    }
                }
            }

            foreach (var property in properties)
            {
                var matchingEntity = default(EntityRecommendation);
                var matchingEntities = default(IEnumerable<EntityRecommendation>);

                // Find using property name
                if (matchingEntities == null || !matchingEntities.Any())
                {
                    matchingEntities = entities.Where(e => e.Type == property.Name);
                }

                // If callback available then use it
                if (matchingEntities.Count() > 1)
                {
                    if (entityExtractor != null)
                    {
                        matchingEntity = entityExtractor(property, matchingEntities);
                    }
                }
                else
                {
                    matchingEntity = matchingEntities.FirstOrDefault();
                }

                // Prioritize resolution
                if (matchingEntity != null)
                {
                    var paramValue = matchingEntity.Resolution != null && matchingEntity.Resolution.Count > 0
                        ? matchingEntity.Resolution.First().Value
                        : matchingEntity.Entity;

                    result &= AssignValue(action, property, paramValue);
                }
                else if (matchingEntities.Count() > 0
                    && matchingEntities.Count(e => e.Resolution != null && e.Resolution.First().Value is JArray) == matchingEntities.Count())
                {
                    var paramValues = new JArray();

                    foreach (var currentMatchingEntity in matchingEntities)
                    {
                        var values = currentMatchingEntity.Resolution.First().Value as JArray;
                        foreach (var value in values)
                        {
                            paramValues.Add(value);
                        }
                    }

                    result &= AssignValue(action, property, paramValues);
                }
                else
                {
                    result = false;
                }
            }

            return result;
        }

        [Serializable]
        public class QueryValueResult
        {
            public QueryValueResult(bool succeed)
            {
                this.Succeed = succeed;
            }

            public ILuisAction NewAction { get; set; }

            public string NewIntent { get; set; }

            public bool Succeed { get; private set; }
        }
    }
}
