# MemoizeRedis

This project enables the Memoization (caching) of Method calls to a redis database.
It is very basic at the moment meant mostly for internal usage but I decided to publish
as it might be helpful for others as I had to overcome some trouble.

The project is not performance optimized and relies on some slow things (reflection).

It will probably undergo rewrites as I'd like to move this to a sourcecode generated 
Attribute (AOS) style caching of method calls but haven't had the time.

Memoization Settings can be passed to the instance, or they will be loaded by default 
from appsettings.json to point toward your redis cache.

    {
      "cache": {
        "server": "172.18.39.138",
        "ttl": 24
        }
    }

The program relies on some lazy "singleton" creation just to make my usage of it easier 
in various projects.

Setup your appsettings.json like above, and then you can call any function like: 

    using MemoizeRedis;
 
    var returnValue = await Memoize.WithRedisAsync(() => AnyFunction(someParam, someParam2));

MemoizeRedis will check the Redis for a cached copy with the key based on the function name 
and parameters passed in to the function. If no cached copy exists, it will run the function.
Once a return from the function occurs it will Serialize to JSON the results, and save that 
to Redis for future usage in calls with the same function/params.