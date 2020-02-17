namespace StackExchange.Redis.Extensions.Core.Implementations
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using Abstractions;
    using Configuration;
    using Microsoft.Extensions.Logging;

    public class RedisCacheConnectionPoolManager : IRedisCacheConnectionPoolManager
    {
        private readonly ConcurrentBag<Lazy<StateAwareConnection>> connections;
        private readonly ILogger<RedisCacheConnectionPoolManager> logger;
        private readonly RedisConfiguration redisConfiguration;

        public RedisCacheConnectionPoolManager(ILogger<RedisCacheConnectionPoolManager> logger, RedisConfiguration redisConfiguration)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.redisConfiguration = redisConfiguration ?? throw new ArgumentNullException(nameof(redisConfiguration));

            this.connections = new ConcurrentBag<Lazy<StateAwareConnection>>();
        }

        public void Dispose()
        {
            List<Lazy<StateAwareConnection>> activeConnections = this.connections.Where(lazy => lazy.IsValueCreated).ToList();

            foreach (Lazy<StateAwareConnection> connection in activeConnections) connection.Value.Invalidate();

            while (this.connections.IsEmpty == false) this.connections.TryTake(out Lazy<StateAwareConnection> taken);
        }

        public IConnectionMultiplexer GetConnection()
        {
            this.EmitConnections();

            Lazy<StateAwareConnection> response;
            IEnumerable<Lazy<StateAwareConnection>> loadedLazies = this.connections.Where(lazy => lazy.IsValueCreated);

            if (loadedLazies.Count() == this.connections.Count)
                response = this.connections.OrderBy(x => x.Value.TotalOutstanding()).First();
            else
                response = this.connections.First(lazy => !lazy.IsValueCreated);

            this.logger.LogDebug("Picked connection from pool {Connection}.", response.Value);
            ConnectionMultiplexer connectionMultiplexer = response.Value;
            return connectionMultiplexer;
        }

        private void EmitConnection()
        {
            ConfigurationOptions configurationOptions = this.redisConfiguration.ConfigurationOptions;
            ConnectionMultiplexer multiplexer = ConnectionMultiplexer.Connect(configurationOptions);
            if (this.redisConfiguration.ProfilingSessionProvider != null) multiplexer.RegisterProfiler(this.redisConfiguration.ProfilingSessionProvider);

            StateAwareConnection InitializeConnection() => new StateAwareConnection(this.logger, multiplexer, this.EmitConnection);
            this.connections.Add(new Lazy<StateAwareConnection>(InitializeConnection));
        }

        private void EmitConnections()
        {
            this.InvalidateDisconnectedConnections();

            int poolSize = this.redisConfiguration.PoolSize;

            static bool IsInvalidOrDisconnectedConnection(Lazy<StateAwareConnection> lazy) => lazy.IsValueCreated && (lazy.Value.IsValid() == false || lazy.Value.IsConnected() == false);
            int requiredNumOfConnections = poolSize - this.connections.Count(IsInvalidOrDisconnectedConnection);
            if (requiredNumOfConnections <= 0) return;
            this.logger.LogDebug("Emitting {RequiredNumberOfConnections} new connections to the pool.", requiredNumOfConnections);
            for (var i = 0; i < requiredNumOfConnections; i++)
                this.EmitConnection();
        }

        private void InvalidateDisconnectedConnections()
        {
            static bool IsDisconnectedConnection(Lazy<StateAwareConnection> lazy) => lazy.IsValueCreated && lazy.Value.IsConnected() == false;
            List<Lazy<StateAwareConnection>> disconnected = this.connections.Where(IsDisconnectedConnection).ToList();
            disconnected.ForEach(lazy => lazy.Value.Invalidate());
        }

        /// <summary>
        ///     Wraps a <see cref="ConnectionMultiplexer" /> instance. Subscribes to certain events of the
        ///     <see cref="ConnectionMultiplexer" /> object and invalidates it in case the connection transients into a state to be
        ///     considered as permanently disconnected.
        /// </summary>
        internal sealed class StateAwareConnection
        {
            private readonly Action invalidateConnectionCallback;
            private readonly ILogger logger;
            private readonly ConnectionMultiplexer multiplexer;
            private bool invalidated;
            private readonly DateTimeOffset timeCreated = DateTimeOffset.Now;

            /// <summary>
            ///     Initializes a new instance of the <see cref="StateAwareConnection" /> class.
            /// </summary>
            /// <param name="logger">An <see cref="ILogger"/> that is used to trace log-events.</param>
            /// <param name="multiplexer">The <see cref="ConnectionMultiplexer" /> connection object to observe.</param>
            /// <param name="connectionInvalidatedCallback">
            ///     A delegate representing a method that will be called when the give the connection became invalid.
            /// </param>
            public StateAwareConnection(ILogger logger, ConnectionMultiplexer multiplexer, Action connectionInvalidatedCallback)
            {
                this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
                this.multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
                this.invalidateConnectionCallback = connectionInvalidatedCallback ?? throw new ArgumentNullException(nameof(connectionInvalidatedCallback));

                this.multiplexer.ConnectionFailed += this.ConnectionFailed;
            }

            public long TotalOutstanding() => this.multiplexer.GetCounters().TotalOutstanding;

            private void ConnectionFailed(object sender, ConnectionFailedEventArgs e)
            {
                this.logger.LogError("Received connection-failed event of type {FailureType}.", e.FailureType);

                switch (e.FailureType)
                {
                    case ConnectionFailureType.ConnectionDisposed:
                    case ConnectionFailureType.InternalFailure:
                    case ConnectionFailureType.SocketClosed:
                    case ConnectionFailureType.SocketFailure:
                    case ConnectionFailureType.UnableToConnect:
                    {
                        this.Invalidate();
                        this.invalidateConnectionCallback();
                        break;
                    }
                }
            }

            public static implicit operator ConnectionMultiplexer(StateAwareConnection c) => c.multiplexer;

            public void Invalidate()
            {
                if (this.invalidated)
                    return;

                double age = DateTimeOffset.Now.Subtract(this.timeCreated).TotalSeconds;
                this.logger.LogDebug("Invalidating connection (age {AgeSeconds} s).", age);

                this.invalidated = true;
                this.multiplexer.ConnectionFailed -= this.ConnectionFailed;
                this.multiplexer?.Dispose();
            }

            public bool IsConnected() => this.multiplexer.IsConnecting == false;
            public bool IsValid() => this.invalidated == false;

            public override string ToString()
            {
                double age = DateTimeOffset.Now.Subtract(this.timeCreated).TotalSeconds;
                string validString = this.IsValid() ? "yes" : "no";
                string connectedString = this.IsConnected() ? "yes" : "no";
                return $"Connection age (s): {age}, Valid: {validString}, Connected; {connectedString}";
            }
        }
    }
}