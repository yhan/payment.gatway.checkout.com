﻿using System;
using System.Threading;
using System.Threading.Tasks;
using AcquiringBanks.Stub;
using Microsoft.Extensions.Logging;
using PaymentGateway.Domain;
using Polly;

namespace PaymentGateway.Infrastructure
{
    /// <inheritdoc cref="IProcessPayment" />
    public class PaymentProcessor : IProcessPayment
    {
        private readonly IThrowsException _gatewayExceptionSimulator;
        private readonly ILogger<PaymentProcessor> _logger;
        private readonly IEventSourcedRepository<Payment> _paymentsRepository;
        private readonly IProvideTimeout _timeoutProviderForBankResponseWaiting;
        private readonly IKnowBufferAndReprocessPaymentRequest _failureHandler;
        private readonly ILogger<RespondedBankStrategy> _bankResponseProcessingLogger;

        public PaymentProcessor(IEventSourcedRepository<Payment> paymentsRepository,
            ILogger<PaymentProcessor> logger,
            IProvideTimeout timeoutProviderForBankResponseWaiting,
            IKnowBufferAndReprocessPaymentRequest failureHandler,

            ILogger<RespondedBankStrategy> bankResponseProcessingLogger,
            
            IThrowsException gatewayExceptionSimulator = null)
        {
            _paymentsRepository = paymentsRepository;
            _logger = logger;
            _timeoutProviderForBankResponseWaiting = timeoutProviderForBankResponseWaiting;
            _failureHandler = failureHandler;
            _bankResponseProcessingLogger = bankResponseProcessingLogger;
            _gatewayExceptionSimulator = gatewayExceptionSimulator;
        }

        public async Task<IPaymentResult> AttemptPaying(IAdaptToBank bankAdapter, Payment payment)
        {
            var payingAttempt = payment.MapToAcquiringBank();

            void OnBreak(Exception exception, TimeSpan timespan, Context context)
            {
                _failureHandler.Buffer(bankAdapter, payingAttempt, payment);
            }

            void OnReset(Context context)
            {
#pragma warning disable 4014
                // Fire and forget (Polly OnReset does not provide awaitable signature)
                _failureHandler.ProcessBufferedPaymentRequest();

#pragma warning restore 4014
            }

            var breaker = Policy
                .Handle<TaskCanceledException>()
                .Or<FailedConnectionToBankException>()
                .CircuitBreakerAsync(exceptionsAllowedBeforeBreaking: 2,
                    durationOfBreak: TimeSpan.FromMilliseconds(20),
                    onBreak: OnBreak,
                    onReset: OnReset);

            var fallbackAndCircuitBreakerConfiguredPolicy = Policy<IPaymentResult>
                .Handle<Exception>()
                .FallbackAsync<IPaymentResult>(cancel => ReturnWillHandleLater(payment.GatewayPaymentId, payment.RequestId))
                .WrapAsync(breaker);

            await Task.Delay(5);

            var attemptPaying = await fallbackAndCircuitBreakerConfiguredPolicy.ExecuteAsync(async () =>
            {
                return await RetriedGettingPaymentResult(bankAdapter, payment, payingAttempt);
            });

           

            return attemptPaying;
        }


        private static async Task<IPaymentResult> ReturnWillHandleLater(Guid gatewayPaymentId, Guid requestPaymentId)
        {
            return await Task.FromResult(new WillHandleLaterPaymentResult(PaymentStatus.WillHandleLater, gatewayPaymentId, requestPaymentId));
        }

        private async Task<PaymentResult> RetriedGettingPaymentResult(IAdaptToBank bankAdapter, Payment payment, PayingAttempt payingAttempt)
        {
            IBankResponse bankResponse = new NullBankResponse();
            // Connection to bank
            var policy = Policy.Handle<TaskCanceledException>()
                .WaitAndRetryAsync(3, retry => TimeSpan.FromMilliseconds(Math.Pow(2, retry)));

            var policyResult = await policy.ExecuteAndCaptureAsync(async () =>
            {
                using (var cts = new CancellationTokenSource())
                {
                    var timeout = _timeoutProviderForBankResponseWaiting.GetTimeout();
                    cts.CancelAfter(timeout);

                    bankResponse = await bankAdapter.RespondToPaymentAttempt(payingAttempt, cts.Token);
                }
            });

            if (policyResult.FinalException != null)
            {
                if (policyResult.FinalException is TaskCanceledException)
                {
                    _logger.LogError($"Payment gatewayId='{payingAttempt.GatewayPaymentId}' requestId='{payingAttempt.PaymentRequestId}' Timeout");

                    payment.Timeout();
                    await _paymentsRepository.Save(payment, payment.Version);

                    return PaymentResult.Fail(payingAttempt.GatewayPaymentId, payingAttempt.PaymentRequestId, policyResult.FinalException, "Timeout");
                }

                if (policyResult.FinalException is BankPaymentDuplicatedException paymentDuplicatedException)
                {
                    _logger.LogError(paymentDuplicatedException.Message);

                    payment.HandleBankPaymentIdDuplication();
                    await _paymentsRepository.Save(payment, payment.Version);

                    return PaymentResult.Fail(payingAttempt.GatewayPaymentId, payingAttempt.PaymentRequestId, policyResult.FinalException, "Timeout");
                }
            }

            var strategy = Build(bankResponse, _paymentsRepository);

            await strategy.Handle(_gatewayExceptionSimulator, bankResponse.GatewayPaymentId);

            return PaymentResult.Finished(payingAttempt.GatewayPaymentId, payingAttempt.PaymentRequestId);
        }
        
        private IHandleBankResponseStrategy Build(IBankResponse bankResponse, IEventSourcedRepository<Payment> paymentsRepository)
        {
            switch (bankResponse)
            {
                case BankResponse response:
                    return new RespondedBankStrategy(response, paymentsRepository, _bankResponseProcessingLogger);

                case BankDoesNotRespond _:
                    return new NotRespondedBankStrategy(paymentsRepository);
            }

            throw new ArgumentException();
        }
    }


    internal class PaymentRequestBuffer
    {
        public IAdaptToBank BankAdapter { get; }
        public PayingAttempt PayingAttempt { get; }
        public Payment Payment { get; }

        public PaymentRequestBuffer(IAdaptToBank bankAdapter, PayingAttempt payingAttempt, Payment payment)
        {
            BankAdapter = bankAdapter;
            PayingAttempt = payingAttempt;
            Payment = payment;
        }
    }
}