using System.Threading.Tasks;

namespace WeatherForecast.LuisActions
{
    public interface ILuisAction
    {
        Task<object> FulfillAsync();
    }
}