﻿#define TEST_ORDER_CREATION_MODE // Test order flag. See details: https://github.com/binance/binance-spot-api-docs/blob/master/rest-api.md#test-new-order-trade

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Spot;
using BinanceBot.Market.Core;
using BinanceBot.Market.Strategies;
using CryptoExchange.Net.Objects;
using NLog;


namespace BinanceBot.Market
{
    /// <summary>
    /// Market Maker Bot
    /// </summary>
    public class MarketMakerBot : BaseMarketBot<NaiveMarketMakerStrategy>
    {
        private readonly IBinanceClient _binanceRestClient;
        private readonly IBinanceSocketClient _webSocketClient;
        private readonly MarketDepth _marketDepth;


        /// <param name="symbol"></param>
        /// <param name="marketStrategy"></param>
        /// <param name="webSocketClient"></param>
        /// <param name="logger"></param>
        /// <param name="binanceRestClient"></param>
        /// <exception cref="ArgumentNullException"><paramref name="symbol"/> cannot be <see langword="null"/></exception>
        /// <exception cref="ArgumentNullException"><paramref name="marketStrategy"/> cannot be <see langword="null"/></exception>
        /// <exception cref="ArgumentNullException"><paramref name="webSocketClient"/> cannot be <see langword="null"/></exception>
        /// <exception cref="ArgumentNullException"><paramref name="binanceRestClient"/> cannot be <see langword="null"/></exception>
        public MarketMakerBot(
            string symbol,
            NaiveMarketMakerStrategy marketStrategy,
            IBinanceClient binanceRestClient,
            IBinanceSocketClient webSocketClient,
            Logger logger) :
            base(symbol, marketStrategy, logger)
        {
            _marketDepth = new MarketDepth(symbol);
            _binanceRestClient = binanceRestClient ?? throw new ArgumentNullException(nameof(binanceRestClient));
            _webSocketClient = webSocketClient ?? throw new ArgumentNullException(nameof(webSocketClient));
        }



        public override async Task ValidateServerTimeAsync()
        {
            CallResult<long> testConnectResponse = await _binanceRestClient.SpotApi.ExchangeData.PingAsync().ConfigureAwait(false);
            
            if (testConnectResponse.Error != null) 
                Logger.Error(testConnectResponse.Error.Message);
            else
            {
                string msg = $"Connection was established successfully. Approximate ping time: {testConnectResponse.Data} ms";
                if (testConnectResponse.Data > 1000)
                    Logger.Warn(msg);
                else
                    Logger.Info(msg);
            }
        }


        public override async Task<IEnumerable<BinanceOrder>> GetOpenedOrdersAsync(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
                throw new ArgumentException("Invalid symbol value", nameof(symbol));

            var response = await _binanceRestClient.SpotApi.Trading.GetOpenOrdersAsync(symbol).ConfigureAwait(false);
            return response.Data;
        }


        public override async Task CancelOrdersAsync(IEnumerable<BinanceOrder> orders)
        {
            if (orders == null)
                throw new ArgumentNullException(nameof(orders));

            foreach (var order in orders)
                await _binanceRestClient.SpotApi.Trading.CancelOrderAsync(orderId: order.Id, origClientOrderId: order.ClientOrderId, symbol: order.Symbol).ConfigureAwait(false);
        }


        public override async Task<BinancePlacedOrder> CreateOrderAsync(CreateOrderRequest order)
        {
            if (order == null) throw new ArgumentNullException(nameof(order));

            #if TEST_ORDER_CREATION_MODE
            WebCallResult<BinancePlacedOrder> response = await _binanceRestClient.SpotApi.Trading.PlaceTestOrderAsync(
                    // general
                    order.Symbol,
                    order.Side,
                    order.OrderType,
                    // price-quantity
                    price: order.Price,
                    quantity: order.Quantity,
                    // metadata
                    newClientOrderId: order.NewClientOrderId,
                    timeInForce: order.TimeInForce,
                    receiveWindow: order.RecvWindow)
                .ConfigureAwait(false);
            #else
            WebCallResult<BinancePlacedOrder> response = await _binanceRestClient.SpotApi.Trading.PlaceOrderAsync(
                    // general
                    order.Symbol, 
                    order.Side, 
                    order.OrderType,
                    // price-quantity
                    price: order.Price,
                    quantity: order.Quantity,
                    // metadata
                    newClientOrderId: order.NewClientOrderId,
                    timeInForce: order.TimeInForce,
                    receiveWindow: order.RecvWindow)
                .ConfigureAwait(false);
            #endif
            
            if (response.Error != null)
                Logger.Error(response.Error.Message);

            return response.Data;
        }



        #region Run bot section
        public override async Task RunAsync()
        {
            // validate connection w/ stock
            await ValidateServerTimeAsync();

            // subscribe on order book updates
            _marketDepth.MarketBestPairChanged += async (s, e) => await OnMarketBestPairChanged(s, e);


            var marketDepthManager = new MarketDepthManager(_binanceRestClient, _webSocketClient);

            // stream order book updates
            marketDepthManager.StreamUpdates(_marketDepth, TimeSpan.FromMilliseconds(1000));
            // build order book
            await marketDepthManager.BuildAsync(_marketDepth, 100);
        } 


        private async Task OnMarketBestPairChanged(object sender, MarketBestPairChangedEventArgs e)
        {
            if (e == null)
                throw new ArgumentNullException(nameof(e));

            //  get current opened orders by token
            var openOrdersResponse = await GetOpenedOrdersAsync(Symbol);

            // cancel already opened orders (if necessary)
            if (openOrdersResponse != null) await CancelOrdersAsync(openOrdersResponse);

            // find new market position
            Quote q = MarketStrategy.Process(e.MarketBestPair);
            // if position found then create order 
            if (q != null) 
            {
                var newOrderRequest = new CreateOrderRequest
                {
                    // general
                    Symbol = Symbol,
                    Side = q.Direction,
                    OrderType = SpotOrderType.Limit,
                    // price-quantity
                    Price = Decimal.Round(q.Price, decimals: MarketStrategy.Config.PricePrecision),
                    Quantity = Decimal.Round(q.Volume, decimals: MarketStrategy.Config.QuoteAssetPrecision),
                    // metadata
                    NewClientOrderId = "test",
                    TimeInForce = TimeInForce.GoodTillCanceled,
                    RecvWindow = (int)MarketStrategy.Config.ReceiveWindow.TotalMilliseconds
                };

                var createOrderResponse = await CreateOrderAsync(newOrderRequest);
                if (createOrderResponse != null)
                    Logger.Warn($"Limit order was created. Price: {createOrderResponse.Price}. Volume: {createOrderResponse.Quantity}");
            }
        }
        #endregion


        #region Stop/dispose bot section
        public override void Stop()
        {
            Logger.Warn("Bot was stopped");
            Dispose();
        }


        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                _webSocketClient.Dispose();
        }
        #endregion
    }
}