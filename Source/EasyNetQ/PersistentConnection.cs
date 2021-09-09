using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EasyNetQ.Events;
using EasyNetQ.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EasyNetQ
{
    /// <summary>
    ///     An abstraction on top of connection which manages its persistence and allows to open channels
    /// </summary>
    public interface IPersistentConnection : IDisposable
    {
        /// <summary>
        ///     True if a connection is connected
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        ///     Creates a new channel
        /// </summary>
        /// <returns>New channel</returns>
        IModel CreateModel();

        void Connect();
    }

    /// <inheritdoc />
    public class PersistentConnection : IPersistentConnection
    {
        private readonly object mutex = new object();
        private readonly ConnectionConfiguration configuration;
        private readonly IConnectionFactory connectionFactory;
        private readonly IEventBus eventBus;
        private readonly ILog logger = LogProvider.For<PersistentConnection>();
        private volatile IAutorecoveringConnection initializedConnection;
        private volatile bool disposed;
        private volatile List<IModel> models = new List<IModel>();

        /// <summary>
        ///     Creates PersistentConnection
        /// </summary>
        /// <param name="configuration">The configuration</param>
        /// <param name="connectionFactory">The connection factory</param>
        /// <param name="eventBus">The event bus</param>
        public PersistentConnection(
            ConnectionConfiguration configuration, IConnectionFactory connectionFactory, IEventBus eventBus
        )
        {
            Preconditions.CheckNotNull(configuration, "configuration");
            Preconditions.CheckNotNull(connectionFactory, "connectionFactory");
            Preconditions.CheckNotNull(eventBus, "eventBus");

            this.configuration = configuration;
            this.connectionFactory = connectionFactory;
            this.eventBus = eventBus;
        }

        public void Connect() {
            initializedConnection = Initialize();
        }

        private IAutorecoveringConnection Initialize() {

            var connection = initializedConnection;
            if (connection == null)
                lock (mutex)
                    connection = initializedConnection ??= ConnectInternal();
            return connection;
        }

        /// <inheritdoc />
        public IModel CreateModel()
        {
            initializedConnection = Initialize();
            if (!initializedConnection.IsOpen)
                throw new EasyNetQException("PersistentConnection: Attempt to create a channel while being disconnected.");

            return GetModel(initializedConnection);
        }

        /// <inheritdoc />
        public bool IsConnected
        {
            get
            {
                var connection = initializedConnection;
                return connection != null && connection.IsOpen;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed) return;

            disposed = true;

            var connection = Interlocked.Exchange(ref initializedConnection, null);
            if (connection != null)
            {
                connection.RecoverySucceeded -= OnConnectionRecovered;
                connection.ConnectionUnblocked -= OnConnectionUnblocked;
                connection.ConnectionBlocked -= OnConnectionBlocked;
                connection.ConnectionShutdown -= OnConnectionShutdown;
                connection.Dispose();
            }
        }

        private IModel GetModel(IAutorecoveringConnection connection)
        {
            if (connection.ChannelMax == 0) {
                // unlimited channels available: no need to manage channels
                return connection.CreateModel();
            }

            lock (mutex)
            {                   
                if (models.Count < connection.ChannelMax)
                {
                    var model = connection.CreateModel();
                    model.ModelShutdown += (sender, args) => models.Remove(model);
                    models.Add(model);

                    return model;
                }
                else
                {
                    return models.Last();
                }
            }
        }

        private IAutorecoveringConnection ConnectInternal()
        {
            var endpoints = configuration.Hosts.Select(x =>
            {
                var endpoint = new AmqpTcpEndpoint(x.Host, x.Port);
                if (x.Ssl.Enabled)
                    endpoint.Ssl = x.Ssl;
                else if (configuration.Ssl.Enabled)
                    endpoint.Ssl = configuration.Ssl;
                return endpoint;
            }).ToArray();

            var connection = connectionFactory.CreateConnection(endpoints) as IAutorecoveringConnection;
            if (connection == null)
                throw new NotSupportedException("Non-recoverable connection is not supported");

            connection.ConnectionShutdown += OnConnectionShutdown;
            connection.ConnectionBlocked += OnConnectionBlocked;
            connection.ConnectionUnblocked += OnConnectionUnblocked;
            connection.RecoverySucceeded += OnConnectionRecovered;

            logger.InfoFormat(
                "Connected to broker {broker}, port {port}",
                connection.Endpoint.HostName,
                connection.Endpoint.Port
            );

            eventBus.Publish(new ConnectionCreatedEvent(connection.Endpoint));

            return connection;
        }

        private void OnConnectionRecovered(object sender, EventArgs e)
        {
            var connection = (IConnection)sender;
            logger.InfoFormat(
                "Reconnected to broker {host}:{port}",
                connection.Endpoint.HostName,
                connection.Endpoint.Port
            );
            eventBus.Publish(new ConnectionRecoveredEvent(connection.Endpoint));
        }

        private void OnConnectionShutdown(object sender, ShutdownEventArgs e)
        {
            var connection = (IConnection)sender;
            logger.InfoFormat(
                "Disconnected from broker {host}:{port} because of {reason}",
                connection.Endpoint.HostName,
                connection.Endpoint.Port,
                e.ReplyText
            );
            eventBus.Publish(new ConnectionDisconnectedEvent(connection.Endpoint, e.ReplyText));
        }

        private void OnConnectionBlocked(object sender, ConnectionBlockedEventArgs e)
        {
            logger.InfoFormat("Connection blocked with reason {reason}", e.Reason);
            eventBus.Publish(new ConnectionBlockedEvent(e.Reason));
        }

        private void OnConnectionUnblocked(object sender, EventArgs e)
        {
            logger.InfoFormat("Connection unblocked");
            eventBus.Publish(new ConnectionUnblockedEvent());
        }
    }
}
