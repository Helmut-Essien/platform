using Platform.Api.Configuration;
using Xunit;

namespace API.Tests;

public class RedisConnectionStringNormalizerTests
{
    [Theory]
    [InlineData("redis://red-d9bp5n3tqb8s73d550a0:6379", "red-d9bp5n3tqb8s73d550a0:6379,abortConnect=false")]
    [InlineData("rediss://default:s3cret@redis.example.com:6380", "redis.example.com:6380,abortConnect=false,user=default,password=s3cret,ssl=True")]
    [InlineData("localhost:6379", "localhost:6379,abortConnect=false")]
    [InlineData("localhost:6379,abortConnect=true", "localhost:6379,abortConnect=true")]
    public void Normalize_ProducesStackExchangeCompatibleString(string input, string expected)
    {
        Assert.Equal(expected, RedisConnectionStringNormalizer.Normalize(input));
    }

    [Fact]
    public void ToConfigurationOptions_DoesNotDuplicatePortForRedisUri()
    {
        var options = RedisConnectionStringNormalizer.ToConfigurationOptions(
            "redis://red-d9bp5n3tqb8s73d550a0:6379");

        Assert.False(options.AbortOnConnectFail);
        var endpoint = Assert.Single(options.EndPoints).ToString();
        Assert.Contains("red-d9bp5n3tqb8s73d550a0:6379", endpoint);
        Assert.DoesNotContain(":6379:6379", endpoint);
    }
}
