using Microsoft.Extensions.Configuration;
using RedisCore;
using System;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using Serilog;

namespace MemoizeRedis
{
    public class Memoize: IDisposable
    {

        private static Memoize _instance { get; set; }

        public static Memoize Instance { 
            get { 
                if (_instance is null)
                    _instance = new Memoize();

                return _instance;
            } 
        }

        public ILogger Logger { get; set; }
        private IPEndPoint _endpoint { get; set; }
        private RedisClient _redis { get; set; }
        private Settings _settings { get; init; }
        public static async Task<T> WithRedisAsync<T>(Expression<Func<Task<T>>> functionToCache)
        {
            var body = (System.Linq.Expressions.MethodCallExpression)functionToCache.Body;
            string argumentString = ArgumentsToString(body);

            // Generate a hashed key for storage based on the expression body
            string key = GetHash(body.Method.Name + argumentString);

            // Check If We have a Cached Copy if so we return it.
            string resultJson = await CheckCacheForKey(key);
            if (resultJson != "")
                return JsonSerializer.Deserialize<T>(resultJson);

            // We didn't have a cached copy let's run our expression
            T resultFresh = await functionToCache.Compile().Invoke();
            await SaveToCache<T>(key, resultFresh);
            return resultFresh;
        }

        public static async Task<T> WithRedisAsync<T>(Expression<Func<T>> functionToCache)
        {
            var body = (System.Linq.Expressions.MethodCallExpression)functionToCache.Body;
            string argumentString = ArgumentsToString(body);

            // Generate a hashed key for storage based on the expression body
            string key = GetHash(body.Method.Name + argumentString);

            // Check If We have a Cached Copy if so we return it.
            string resultJson = await CheckCacheForKey(key);
            if (resultJson != "")
                return JsonSerializer.Deserialize<T>(resultJson);

            // We didn't have a cached copy let's run our expression
            T resultFresh = functionToCache.Compile().Invoke();
            await SaveToCache<T>(key, resultFresh);
            return resultFresh;
        }

        private static async Task<string> CheckCacheForKey(string key)
        {
            string resultJson = "";

            try
            {
                // Check if we have a cached copy in redis
                Optional<string> result = await Instance._redis.Get<string>(key);
                if (result.HasValue)
                {
                    Instance.Logger.Information("Redis Cache Returned for {key}", key);
                    resultJson = result.Value;
                }
            }
            catch (Exception err)
            {
                Instance.Logger.Warning("Failed to get cache from Redis Server ({server}): {error}", Instance._endpoint.ToString(), err.Message);
            }

            return resultJson;
        }

        private static async Task SaveToCache<T>(string key, T resultFresh)
        {
            try
            {
                // Since we have a result let's cache this value
                var jsonValue = JsonSerializer.Serialize(resultFresh);
                await Instance._redis.Set<string>(key, jsonValue, TimeSpan.FromHours(Instance._settings.TTL));
                Instance.Logger.Information("New Cacheable Result for {key}", key);

            }
            catch (Exception err)
            {
                Instance.Logger.Warning("Failed to store cache to Redis Server ({server}): {error}", Instance._endpoint.ToString(), err.Message);
            }
        }

        private static string ArgumentsToString(MethodCallExpression body)
        {
            var arguments = "";
            foreach (var argument in body.Arguments)
            {
                if (argument.NodeType == ExpressionType.Constant || argument.NodeType == ExpressionType.NewArrayInit)
                {
                    arguments = arguments + argument.ToString();
                    continue;
                }

                var exp = ResolveMemberExpression(argument);

                if (exp is not null)
                {
                    var value = GetValue(exp);
                    arguments = arguments + JsonSerializer.Serialize(value);
                }
                else
                {
                    try
                    {
                        arguments = arguments + JsonSerializer.Serialize(argument.ToString());
                    }
                    catch { }
                }
            }

            return arguments;
        }

        public static MemberExpression ResolveMemberExpression(Expression expression)
        {

            if (expression is MemberExpression)
            {
                return (MemberExpression)expression;
            }
            else if (expression is UnaryExpression)
            {
                // if casting is involved, Expression is not x => x.FieldName but x => Convert(x.Fieldname)
                return (MemberExpression)((UnaryExpression)expression).Operand;
            }
            return null;
        }

        private static object GetValue(MemberExpression exp)
        {
            // expression is ConstantExpression or FieldExpression
            if (exp.Expression is ConstantExpression)
            {
                return ((ConstantExpression)exp.Expression)?.Value
                        ?.GetType()
                        ?.GetField(exp.Member.Name)
                        ?.GetValue(((ConstantExpression)exp.Expression).Value);
            }
            else if (exp.Expression is MemberExpression)
            {
                return GetValue((MemberExpression)exp.Expression);
            }
            else
            {
                return null;
            }
        }

        private static string GetHash(string value)
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(valueBytes);
            sha256.Dispose();
            return Convert.ToBase64String(hash);
        }

        public Memoize(Settings cacheSettings = null,  ILogger log = null)
        {
            if (cacheSettings is null)
                _settings = LoadConfiguration();

            if (log is null)
                this.Logger = new LoggerConfiguration()
                .CreateLogger();

            _endpoint = new IPEndPoint(IPAddress.Parse(_settings.Server), 6379);
            _redis = new RedisClient(_endpoint);
        }

        private Settings LoadConfiguration()
        {
            var configuration = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
              .Build();

            return configuration.GetSection("cache").Get<Settings>();
        }

        public void Dispose()
        {
            _redis.Dispose();
        }
    }
}
