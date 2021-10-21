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
using System.Reflection;

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
        private SHA256 _sha256 { get; init; }

        public static async Task<T> WithRedisAsync<T>(Expression<Func<T>> functionToCache)
        {
            var body = (System.Linq.Expressions.MethodCallExpression)functionToCache.Body;
            string argumentString = ArgumentsToString(body);

            // Generate a hashed key for storage based on the expression body
            string key = GetHash(Instance._sha256, body.Method.Name + argumentString);

            Optional<string> result;
            try
            {
                // Check if we have a cached copy in redis
                result = await Instance._redis.Get<string>(key);
                if (result.HasValue)
                {
                    Instance.Logger.Information("Redis Cache Returned for {key}", key);
                    return JsonSerializer.Deserialize<T>(result.Value);
                }
            }
            catch (Exception err)
            {
                Instance.Logger.Warning("Failed to get cache from Redis Server ({server}): {error}", Instance._endpoint.ToString(), err.Message);
            }

            // We didn't have a cached copy let's run our expression
            T resultFresh = functionToCache.Compile().Invoke();

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

            // Return our fresh copy result.
            return resultFresh;
        }

        private static string ArgumentsToString(MethodCallExpression body)
        {
            var arguments = "";
            foreach (var argument in body.Arguments)
            {
                if (argument.NodeType == ExpressionType.Constant)
                {
                    arguments = arguments + argument.ToString();
                    continue;
                }

                var exp = ResolveMemberExpression(argument);

                var value = GetValue(exp);
                arguments = arguments + JsonSerializer.Serialize(value);

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
                throw new NotImplementedException();
            }
        }

        private static string GetHash(SHA256 sha256, string value)
        {
            var valueBytes = Encoding.UTF8.GetBytes(value);
            var hash = sha256.ComputeHash(valueBytes);
            return Convert.ToBase64String(hash);
        }

        public Memoize(Settings cacheSettings = null,  ILogger log = null)
        {
            if (cacheSettings is null)
                _settings = LoadConfiguration();

            if (log is null)
                this.Logger = new LoggerConfiguration()
                .WriteTo.File("MemoizeRedis.log")
                .CreateLogger();

            _endpoint = new IPEndPoint(IPAddress.Parse(_settings.Server), 6379);
            _redis = new RedisClient(_endpoint);
            _sha256 = SHA256.Create();
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
            _sha256.Dispose();
        }
    }
}
