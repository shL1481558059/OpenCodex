using System.Linq.Expressions;
using OpenCodex.Core.Domain;
using OpenCodex.Core.Errors;
using OpenCodex.Core.Protocols;
using OpenCodex.Core.Services.WebSearch;
using OpenCodex.CoreBase.Abstractions;
using OpenCodex.CoreBase.Data;
using OpenCodex.CoreBase.Domain.WebSearch;
using Xunit;

namespace OpenCodex.Api.Tests;

public sealed class WebSearchSimulatorStreamFailoverTests
{
    [Fact]
    public async Task RunChatStreamAsync_Upstream429BeforeFirstLine_EmitsNothing()
    {
        var simulator = new WebSearchSimulator(
            new ThrowingUpstreamClient(),
            new StubWebSearchClient(),
            new StubRepository<WebSearchSettings>(),
            new StubRepository<TavilyKey>());

        var lines = new List<string>();
        UpstreamException? thrown = null;
        try
        {
            await foreach (var line in simulator.RunChatStreamAsync(
                CreateChannel(),
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["model"] = "upstream-model"
                },
                CreatePayload(),
                "shared-model",
                120,
                new WebSearchStreamResult(),
                streamCapture: null,
                CancellationToken.None))
            {
                lines.Add(line);
            }
        }
        catch (UpstreamException exception)
        {
            thrown = exception;
        }

        Assert.NotNull(thrown);
        Assert.Equal(ProxyHttpStatus.TooManyRequests, thrown!.StatusCode);
        Assert.Empty(lines);
    }

    private static Dictionary<string, object?> CreateChannel()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "primary",
            ["name"] = "primary",
            ["type"] = ProtocolConverter.Chat
        };
    }

    private static Dictionary<string, object?> CreatePayload()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = "shared-model",
            ["input"] = "ping",
            ["max_tool_calls"] = 2,
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = WebSearchRequestPolicy.ToolName
                }
            }
        };
    }

    private sealed class ThrowingUpstreamClient : IUpstreamClient
    {
        public Task<Dictionary<string, object?>> PostJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<string> StreamJsonAsync(
            IReadOnlyDictionary<string, object?> channel,
            IReadOnlyDictionary<string, object?> payload,
            int defaultTimeout,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            throw new UpstreamException(
                "upstream returned HTTP 429",
                ProxyHttpStatus.TooManyRequests);
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class StubWebSearchClient : IWebSearchClient
    {
        public Task<WebSearchProviderResult> SearchAsync(
            WebSearchProviderKey key,
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WebSearchProviderResult(
                false,
                ProxyHttpStatus.TooManyRequests,
                0,
                "rate_limit_exceeded",
                "quota",
                new WebSearchSummary("", [], "quota"),
                null));
        }
    }

    private sealed class StubRepository<T> : IRepository<T>
        where T : BaseEntity
    {
        private readonly List<T> _items = [];

        public IQueryable<T> Table => _items.AsQueryable();

        public IQueryable<T> TableNoTracking => _items.AsQueryable();

        public T? GetById(object id)
        {
            return _items.FirstOrDefault(item => Equals(item.GetId(), id));
        }

        public ValueTask<T?> GetByIdAsync(object id)
        {
            return new ValueTask<T?>(GetById(id));
        }

        public void Insert(T entity)
        {
            _items.Add(entity);
        }

        public Task InsertAsync(T entity)
        {
            Insert(entity);
            return Task.CompletedTask;
        }

        public void Insert(IEnumerable<T> entities)
        {
            _items.AddRange(entities);
        }

        public Task InsertAsync(IEnumerable<T> entities)
        {
            Insert(entities);
            return Task.CompletedTask;
        }

        public void Update(T entity)
        {
        }

        public Task UpdateAsync(T entity)
        {
            return Task.CompletedTask;
        }

        public void Update(T entity, params string[] propNames)
        {
        }

        public Task UpdateAsync(T entity, params string[] propNames)
        {
            return Task.CompletedTask;
        }

        public void Delete(T entity)
        {
            _items.Remove(entity);
        }

        public Task DeleteAsync(T entity)
        {
            Delete(entity);
            return Task.CompletedTask;
        }

        public void Delete(IEnumerable<T> entities)
        {
            foreach (var entity in entities)
            {
                _items.Remove(entity);
            }
        }

        public Task DeleteAsync(IEnumerable<T> entities)
        {
            Delete(entities);
            return Task.CompletedTask;
        }

        public int ExecuteDeleteAll()
        {
            var count = _items.Count;
            _items.Clear();
            return count;
        }

        public int DeleteWhere(Expression<Func<T, bool>> predicate)
        {
            var matches = _items.Where(predicate.Compile()).ToList();
            foreach (var entity in matches)
            {
                _items.Remove(entity);
            }

            return matches.Count;
        }

        public Task<int> DeleteWhereAsync(Expression<Func<T, bool>> predicate)
        {
            return Task.FromResult(DeleteWhere(predicate));
        }

        public int SaveChanges()
        {
            return 0;
        }

        public Task<int> SaveChangesAsync()
        {
            return Task.FromResult(0);
        }
    }
}
