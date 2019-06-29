using System;
using System.Threading.Tasks;
using AcquiringBanks.API;
using Microsoft.AspNetCore.Mvc;
using NFluent;
using NUnit.Framework;
using PaymentGateway.API;
using PaymentGateway.Domain;

namespace PaymentGateway.Tests
{
    [TestFixture]
    public class ReadPaymentShould
    {
        [Test]
        public async Task Return_NotFound_When_Payment_does_not_exist()
        {
            var cqrs = PaymentCQRS.Build(AcquiringBanks.API.BankPaymentStatus.Rejected);
            var nonExistingPaymentId = Guid.NewGuid();
            var actionResult = await cqrs.ReadController.GetPaymentInfo(nonExistingPaymentId);

            Check.That(actionResult.Result).IsInstanceOf<NotFoundResult>();
            Check.That(actionResult.Value).IsNull();
        }

        [Repeat(10)]
        [TestCase(AcquiringBanks.API.BankPaymentStatus.Rejected, PaymentGateway.Domain.PaymentStatus.RejectedByBank)]
        [TestCase(AcquiringBanks.API.BankPaymentStatus.Accepted, PaymentGateway.Domain.PaymentStatus.Success)]
        public async Task Can_retrieve_payment_details_using_BankPaymentId(AcquiringBanks.API.BankPaymentStatus paymentBankStatus, PaymentGateway.Domain.PaymentStatus expectedStatusInPaymentDetails
        )
        {
            var requestId = Guid.NewGuid();
            var paymentRequest =  new PaymentRequest(requestId, "John Smith", "4524 4587 5698 1200", "05/19", new Money("EUR", 42.66),
                "321");

            var gatewayPaymentId = Guid.NewGuid();
            IGenerateGuid guidGenerator = new GuidGeneratorForTesting(gatewayPaymentId);

            var cqrs = PaymentCQRS.Build(paymentBankStatus);
            await cqrs.RequestController.ProceedPaymentRequest(paymentRequest, guidGenerator, cqrs.PaymentIdsMapping, cqrs.PaymentProcessor);


            var payment = (await cqrs.ReadController.GetPaymentInfo(gatewayPaymentId)).Value;
            var paymentDetails = (await cqrs.PaymentDetailsReadController.GetPaymentInfo(payment.AcquiringBankPaymentId)).Value;

            // The response should include a masked card number and card details along with a
            // status code which indicates the result of the payment.
            Check.That(paymentDetails.CreditCardNumber).IsEqualTo("4524 XXXX XXXX XXXX");
            Check.That(paymentDetails.CreditCardHolderName).IsEqualTo("John Smith");
            Check.That(paymentDetails.CreditCardExpiry).IsEqualTo("05/19");
            Check.That(paymentDetails.CreditCardCvv).IsEqualTo("321");
            Check.That(paymentDetails.Status).IsEqualTo(expectedStatusInPaymentDetails);
        }
    }
}