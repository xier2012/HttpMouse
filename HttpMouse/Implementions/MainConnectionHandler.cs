﻿using HttpMouse.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace HttpMouse.Implementions
{
    /// <summary>
    /// 主连接处理者
    /// </summary> 
    sealed class MainConnectionHandler : IMainConnectionHandler
    {
        private const string SERVER_KEY = "ServerKey";
        private const string CLIENT_DOMAIN = "ClientDomain";
        private const string CLIENT_UP_STREAM = "ClientUpstream";

        private readonly IMainConnectionAuthenticator authenticator;
        private readonly ILogger<MainConnectionHandler> logger;
        private readonly ConcurrentDictionary<string, IMainConnection> connections = new();


        /// <summary>
        /// 主连接变化后
        /// </summary>
        public event Action<IMainConnection[]>? ConnectionsChanged;

        /// <summary>
        /// 主连接处理者
        /// </summary>
        /// <param name="authenticator"></param>
        /// <param name="logger"></param>
        public MainConnectionHandler(
            IMainConnectionAuthenticator authenticator,
            ILogger<MainConnectionHandler> logger)
        {
            this.authenticator = authenticator;
            this.logger = logger;
        }

        /// <summary>
        /// 处理连接
        /// </summary>
        /// <param name="context"></param>
        /// <param name="next"></param>
        /// <returns></returns>
        public async Task HandleConnectionAsync(HttpContext context, Func<Task> next)
        {
            if (context.WebSockets.IsWebSocketRequest == false ||
                context.Request.Headers.TryGetValue(SERVER_KEY, out var keyValues) == false ||
                context.Request.Headers.TryGetValue(CLIENT_DOMAIN, out var domainValues) == false ||
                context.Request.Headers.TryGetValue(CLIENT_UP_STREAM, out var upSteramValues) == false ||
                Uri.TryCreate(upSteramValues.ToString(), UriKind.Absolute, out var clientUpstream) == false)
            {
                await next();
                return;
            }

            var clientDomain = domainValues.ToString();
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var connection = new MainConnection(clientDomain, clientUpstream, webSocket);

            // 密钥验证
            var key = keyValues.ToString();
            if (await this.authenticator.AuthenticateAsync(clientDomain, key) == false)
            {
                await connection.CloseAsync("Key不正确");
                return;
            }

            // 验证连接唯一
            if (this.connections.TryAdd(clientDomain, connection) == false)
            {
                await connection.CloseAsync($"已在其它地方存在{clientDomain}的连接实例");
                return;
            }


            this.logger.LogInformation($"{connection}连接过来");
            this.ConnectionsChanged?.Invoke(this.connections.Values.ToArray());

            await connection.WaitingCloseAsync();

            this.logger.LogInformation($"{connection}断开连接");
            this.connections.TryRemove(clientDomain, out _);
            this.ConnectionsChanged?.Invoke(this.connections.Values.ToArray());
        }

        /// <summary>
        /// 尝试获取连接 
        /// </summary>
        /// <param name="clientDomain"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool TryGetValue(string clientDomain, [MaybeNullWhen(false)] out IMainConnection value)
        {
            return this.connections.TryGetValue(clientDomain, out value);
        }
    }
}
