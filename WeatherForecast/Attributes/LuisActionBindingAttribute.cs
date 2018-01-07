using System;

namespace WeatherForecast.Attributes
{
    public class LuisActionBindingAttribute : Attribute
    {
        public string IntentName { get; set; }
        public string IntentDescription { get; set; }

        public LuisActionBindingAttribute(string intentName)
        {
            if (string.IsNullOrEmpty(intentName))
                throw new ArgumentException(nameof(intentName));

            IntentName = intentName;
            IntentDescription = intentName;
        }
    }
}