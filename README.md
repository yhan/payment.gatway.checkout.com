
# Assumptions
1. **Acquiring bank**

   The statement of the exercise text:
    > It also performs some validation of the card information and then **sends the payment details to the appropriate 3rd party organization for processing**.

    1. I suppose the `Acquiring bank` is a component of the Information System, not the real bank. 
    1. I suppose the `Third party` is a legal entity who liaise the **Acquiring bank** and **Issuing bank** and potentially a **Custodian** to complete the payment if eligible.

1. **Determination of Banks**  
   In the payment workflow, we should determine the `Issuing bank` and the `Acquiring bank`.  

   1. **Issuing bank**  
   After have done some research, now I am aware of the fact that we can determine the `Issuing bank` via `card number`.

   1. **Acquiring bank**  
   But when it comes to Acquiring bank, I find no way in the beginning to determine it. The way that can determine the acquiring bank, could be: We associate the `Acquiring bank` to the `Merchant`.  
   Things can be done when we **onboard** a `Merchant`

   The statement of the exercise text, in the payment request does not mention an identifier of the `Merchant`. 
   
   > A payment request should include appropriate fields such as the card number, expiry month/date, amount, currency, and cvv.
   
   The question is how the system can forward a `Payment Request` to the `Merchant`'s Acquiring bank? 

   I added `Merchant`'s id to the `Payment Request`.    

   > **! Disclaimer:**: In real world, it will be not safe to let 
     


# Architecture

## **CQRS**
CQRS is chosen for these *reasons*:  
I am asked to develop two features: Payment request and Payment details retrieval. 
1. They should be scaled differently.
1. Do payment is the core function of Gateway, the benefit of the company may main come from the amount/number of transactions achieved by the platform. We should not disturb the payment request handling by a flood of payment details queries for reporting purpose.

### Implementation
Three components:
- **Write API**: Handle payment requests, saving to `write model`: events. (events: will be explained in [Event sourcing](##EventSourcing) section)

- **Read Projector**: Project `write model` to `read model` which fits read payment details requirement.

- **Read API**: Feed payment retrieval queries.   
  > Here we have only one read model which is asked for. But in real world, we probably have many of them, for performance enhancement. We can imagine that company's revenue comes from **transaction volume**, thus we can imagine a read model that give us live PnL vision as transactions go on.

> **! Disclaimer:** **In real world, above three components should be hosted to 3 separate processes, for scaling easily**. Here for the sake of simplicities of the exercise, I have not implemented neither external storage (events and read model) nor external message bus. It will be hence difficult to separate them to different processes.

Still you can see the embryonic form of the 3 processes.
- [Write API](https://github.com/yhan/payment.gateway.checkout.com/tree/master/src/PaymentGateway/Apps/PaymentGateway.API/Controllers/WriteAPI)

- [Read API](https://github.com/yhan/payment.gateway.checkout.com/tree/master/src/PaymentGateway/Apps/PaymentGateway.API/Controllers/ReadAPI)

- [Read projector](https://github.com/yhan/payment.gateway.checkout.com/tree/master/src/PaymentGateway/Apps/PaymentGateway.API/ReadProjector)


## **EventSourcing**
For a Gateway which handles sensitive financial transactions second to to second, it is critical that we have a full audit trail of what has happened.

Event sourcing also helps constructing CQRS. i.e. we have **always** capabilities to construct diverse and varied read models, as events recorded all information chronologically.

## **Hexagonal**
The motivation of Hexagonal is very general, can be found for example [here](https://apiumhub.com/tech-blog-barcelona/hexagonal-architecture/)



# Design
1. Entity:  
**Payment** represent a financial transaction achieved with the help of a bank payment card. A `Payment` can fail or succeed.

1. **Command handling asynchrony**  
For managing: 
   - unreliable network, unknown bank API availability and latency
   - burst/back pressure: i.e. if we handle `PaymentRequest` synchronously, because of network and long latency, our Gateway may suffer from high I/O waiting, the system will congested. 

   I decided to handle `PaymentRequest` asynchronously. i.e. When `PaymentRequest` arrives, Gateway create immediately a `Payment` resource. The request forwarding and bank response handling are done asynchronously. HTTP status 202 Accepted along with a resource identifier in location header will be returned. Merchant can follow up (polling) the payment with the given address.

   > In real world, we can consider long polling, Server Sent Event or Webhooks.

   In real world, for the sake pragmatism, we can do more *smart* handling. i.e. We can say: if the Gateway get a response from the bank within 50 ms, it returns 201 Created with the `Payment` final status: Accepted or Rejected (by the bank); otherwise returns 202 Accepted.

1. **Anti corruption**:

  - Never put HTTP dto & external library into Domain and never expose domain type to HTTP.
  - Always do adaptation from one world to another.

1. **Event structure: flat**   
no embedded type, for easing event versioning.

1. Simulate I/O, avoid blocking thread pool thread waiting for I/O

1. Anti Corruption  
Never leak external libraries (acquiring bank ones) to Domain Entity / Aggregate, do mapping instead

# Public API

1. **Request a payment**:  
  - **POST api/Payments**
     Endpoint to send payment request.  
     
     Request example:  
     ```json
     {
        "requestId": "ccd8af8e-5a27-40dc-93c5-f19e78984391",
        "merchantId": "2d0ae468-7ac9-48f4-be3f-73628de3600e",
        "card":{
            "number": "4524 4587 5698 1200",
            "Expiry": "05/19",
            "Cvv": "321"
        },
        "amount": {
            "currency": "EUR",
            "amount": 42.66
        }
    }
    ```
   
    Response example:  
    1. 202 Accepted
    ```json
    {
       "gatewayPaymentId": "41b49021-98a2-41cf-80dc-6f87382322f8",
       "acquiringBankPaymentId": null,
       "status": "Pending",
       "requestId": "ccd8af8e-5a27-40dc-93c5-f19e78984391",
       "approved": null
    }
    ```
       and with the header location.

    2. 404 Bad request with the invalidity details, if the request is invalid
    ```json
    {
       "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
       "title": "Invalid request",
       "status": 400,
       "detail": "Invalid card CVV"
    }
    ```

1. **Get payment and payment details**:  

   - **GET api/Payments/{gateWayPaymentId}**  
     Endpoint to retrieving payment status. Write controller redirect to this controller (why? Cf. [Command handling asynchrony](#Design)).  
     Response example:
     ```json
     {
       "gatewayPaymentId": "41b49021-98a2-41cf-80dc-6f87382322f8",
       "acquiringBankPaymentId": "0bfa5d5b-8742-459f-94c9-484d61ad6093",
       "status": "RejectedByBank",
       "requestId": "ccd8af8e-5a27-40dc-93c5-f19e78984391",
       "approved": false
     } 
     ```

   -  **GET api/PaymentsDetails/{acquiringBankPaymentId}**  
      Endpoint to retrieving payment details

      Response example:
      ```json
      {
        "status": "RejectedByBank",
        "acquiringBankPaymentId": "0bfa5d5b-8742-459f-94c9-484d61ad6093",
        "card":{
        "number": "4524 XXXX XXXX XXXX",
        "expiry": "05/19",
        "cvv": "321"
        },
        "approved": false
      }
      ```

1. **Ids**: three types of ids
   - **Payment request id**: Payment unique identifier from merchants. Is part of payment request payload. Cf. C# struct `PaymentRequestId`. In real world, each `Merchant` will send their own format of request unique identifier. We should adapt it to the one of Gateway . For simplicity of exercise, I used `System.Guid`.

   - **Gateway payment id**: Unique identifier of payment in Gateway internal system, Cf. C# struct `Domain.GatewayPaymentId`. 

   - **Acquiring bank payment id**: Unique identifier returned from acquiring banks, Cf C# struct `Domain.AcquiringBankPaymentId`. In real world, each `Acquiring bank` will send their own unique identifer.  We should adapt it to the one of Gateway . For simplicity of exercise, I used `System.Guid`.


# SLA
1. A `PaymentRequestId` will be handled once and only once.

# Tradeoff
1. Identical `PaymentRequest` submitted more than once. We have two options:  
   1. Idempotency: remind client of API that payment has already been created, and it is available at this location.

   1. Reject duplicated  `PaymentRequest`.

   I chose the 2nd.

# Performance
When IGenerateBankPaymentId is configured as `NoDelay`, performances in Performance.xlsx.

To run performance tests:
1. Goto API csproj folder  
1. Run: 
    ```
    Dotnet publish -c Release -r win10-x64
    ```
1. Run the tests in `PaymentGateway.Write.PerformanceTests` and `PaymentGateway.Read.PerformanceTests`

# Unit and Acceptance tests
The coding is entirely test driven.  

Code coverage: 67.17%.  

Non covered codes are:  
 - API bootstrap
 - Performance tests them self
 - Some infrastructure code borrowed from [Greg Young's git repository](https://github.com/gregoryyoung/m-r)
 - Some randomness generation only for production. (Acceptance tests use output deterministic behavior)
 - Properties in acquiring bank stubs, they are there just to show the design.


# Prerequisite for building the solution in Visual Studio
   Ensure that you have .NET Core 2.2 SDK installed. 

   > For Visual Studio 2017 (which I am actually using) compatibility reason, please use https://dotnet.microsoft.com/download/thank-you/dotnet-sdk-2.2.107-windows-x64-installer 

# Improvements

Hereunder some improvements should be definitely done:  

1. I use `Merchant` id to determine its `Acquiring bank` (cf. [Assumptions](#Assumptions)), it is part of `Payment Request` payload. This is not safe. And in a very general way, the exchanges of messages between Gateway and Merchant is not protected by authentication.   

   In real world we should do authentication negotiation to let Gateway to know which `Merchant` I am dialoguing. This can be achieved as follows:  

    1. When we onboard a `Merchant`, we distribute a `secret` in a very safe manner to `Merchant`. 
    1. In all exchanges between `Merchant` to `Gateway`, the secret key should be included in HTTP header 'Authorization' 

1. Alls simulated async, I/O should add timeout cancellation

# Go further
1. Retrieving a payment’s details API
   The exercise text states a basic requirement:  
   > The second requirement for the payment gateway is to allow a merchant to retrieve details of a previously made payment using its identifier.  

   In real world, we may consider adding:
   1. Query for a time window
   1. Query pagination
   1. Other filters

   For achieving query for a time window, I should add payment timestamp to both my `Events` and `Read models`.

1. Require `PaymentRequest` Smart Batching ([here by Martin Thompson](https://github.com/real-logic/aeron/wiki/Design-Principles) or [here](https://blog.scooletz.com/2018/01/22/the-batch-is-dead-long-live-the-smart-batch/))

   The motivations are:
   - Maybe for a merchant, say Amazon, the receives 50,000 payment requests per second from shopper. Batching 5000 requests is an option, because shopper doesn't care about 1s of delay.
   - For our Gateway, we will have less resources to consume, thus improve the performance. 

   > A combination of time window and number of requests can be used to size the Smart Batching.
   
# Open source used:
The event sourcing infrastructure is borrowed from [Greg Young's git repository](https://github.com/gregoryyoung/m-r)

