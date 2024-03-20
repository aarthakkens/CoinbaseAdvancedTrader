using System.Security.Cryptography;
using System.Text;
using Amazon.Lambda.Core;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using RestSharp;

// Run: dotnet lambda invoke-function myDotnetFunctionName
// Update: dotnet lambda deploy-function

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CoinbaseCryptoBuyer;

public class Function
{
    public async Task FunctionHandler(OrderDetails inp, ILambdaContext context)
    {
        var order = new OrderDetails(inp.TradeType, inp.CoinSymbol, inp.OrderSize);
        var createOrder = new CreateOrder(order);

        string response = await createOrder.ExecuteRequestAndGetResponse();
        context.Logger.LogInformation($"\nExecuted: {order.TradeType} ({order.OrderSize}).\nResponse: \n{response}");
    }
}

public record struct OrderDetails(string TradeType, string CoinSymbol, decimal OrderSize);

public class CreateOrder
{
    private RestRequest? _request;
    private int _timeStamp;
    private int _currentFearAndGreedIndex;
    private string? _key;
    private string? _secret;
    private string _coinSymbol;
    private string _tradeTypeJsonInput;
    private string _message = "";
    private decimal _orderSize;
    private decimal _tradeAmount;
    private const string FearAndGreedApiPath = "https://api.alternative.me/fng/";
    private const string CoinbaseApiFullOrderPath = "https://api.coinbase.com/api/v3/brokerage/orders";
    private const string CoinbaseApiOrderPath = "/api/v3/brokerage/orders";

    public CreateOrder(OrderDetails details)
    {
        _tradeTypeJsonInput = details.TradeType.ToUpper();
        _coinSymbol = details.CoinSymbol;
        _orderSize = details.OrderSize;
        _timeStamp = GetUnixTime();
        
        GetKeysFromEnvironmentVariables();
        CalculateOrderAmountBasedOnFearAndGreedIndex();
    }

    public async Task<string> ExecuteRequestAndGetResponse()
    {
        try
        {
            var preparedBody = PrepareBodyForApiRequest();
            string? body = JsonConvert.SerializeObject(preparedBody);

            _request = new RestRequest(CoinbaseApiFullOrderPath, Method.Post);
            _message = _timeStamp.ToString() + "POST" + CoinbaseApiOrderPath + body;

            AddHeadersAndBodyToApiRequest(body);
            
            var client = new RestClient(CoinbaseApiFullOrderPath);
            var response = client.Execute(_request);
            
            return response.Content;
        }
        catch (Exception ex)
        {
            // Get the error from the API and store it in the logs
            return ex.Message;
        }
    }
    
    void GetKeysFromEnvironmentVariables()
    {
        _key = Environment.GetEnvironmentVariable("key");
        _secret = Environment.GetEnvironmentVariable("secret");
    }
    
    void CalculateOrderAmountBasedOnFearAndGreedIndex()
    {
        if (_tradeTypeJsonInput == "BUY")
        {
            // At the moment you can only trade based on the index when using BUY as input
            _currentFearAndGreedIndex = GetFearAndGreedIndexNumber();
            _tradeAmount = CalculateOrderSizeAmount();
        }
    }
    
    void AddHeadersAndBodyToApiRequest(string body)
    {
        if (_request != null)
        {
            _request.AddHeader("accept", "application/json");
            _request.AddHeader("CB-ACCESS-KEY", _key);
            _request.AddHeader("CB-ACCESS-SIGN", GetHmacsha256Signature());
            _request.AddHeader("CB-ACCESS-TIMESTAMP", _timeStamp);
            _request.AddBody(body, "application/json");
        }
    }
    
    dynamic PrepareBodyForApiRequest()
    {
        string actionSide = GetActionSide();
        
        return new
        {
            client_order_id = GetClientId(),
            product_id = _coinSymbol,
            side = actionSide,
            order_configuration = new
            {
                market_market_ioc = GetMarketIoc()
            }
        };
    }

    dynamic GetMarketIoc()
    {
        switch (_tradeTypeJsonInput)
        {
            case "BUY":
            case "BUY-IGNORE-FG":
                return new { quote_size = _tradeAmount.ToString() }; // Use 'quote_size' for BUY
            case "SELL": 
                return new { base_size = _orderSize.ToString() }; // Use 'base_size' for SELL
            default:
                // Notify the JSON input of the trade type is incorrect
                return null;
        }
    }
    
    string GetActionSide()
    {
        if (_tradeTypeJsonInput == "BUY-IGNORE-FG")
        {
            return "BUY";
        }

        return _tradeTypeJsonInput;
    }
    
    int GetFearAndGreedIndexNumber()
    {
        var requestFearGreed = new RestRequest();
        
        var client = new RestClient(FearAndGreedApiPath);
        var response = client.Execute(requestFearGreed).Content;
        var fearGreedIndex = Int32.Parse(JObject.Parse(response)["data"][0]["value"].ToString());

        return fearGreedIndex;
    }

    decimal CalculateOrderSizeAmount()
    {
        var multiplier = MultiplyOrderSizeBasedOnFearAndGreedIndex(_currentFearAndGreedIndex);
        var orderAmount = _orderSize * multiplier;
        
        return Math.Round(orderAmount, 2);
    }

    decimal MultiplyOrderSizeBasedOnFearAndGreedIndex(int fearAndGreedIndex)
    {
        // Change the aggression on the order sizes based off of the fear and greed index
        switch (fearAndGreedIndex)
        {
            case > 0 and < 10:
                return 3m;
            case >= 10 and < 21:
                return 2m;
            case >= 21 and < 31:
                return 1.5m;
            case >= 31 and < 60:
                return 1m;
            case >= 60 and < 80:
                return 0.5m;
            default:
                return 0;
        }
    }
    
    string GetHmacsha256Signature()
    {
        byte[] prehashBytes = Encoding.UTF8.GetBytes(_message);
        byte[] keyBytes = Encoding.UTF8.GetBytes(_secret);

        var hmac = new HMACSHA256(keyBytes);
        byte[] hash2 = hmac.ComputeHash(prehashBytes);

        return BitConverter.ToString(hash2).Replace("-", "").ToLower();
    }

    int GetUnixTime()
    {
        return (int)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds;
    }

    string GetClientId()
    {
        return Guid.NewGuid().ToString();
    }
    
}
