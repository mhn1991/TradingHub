# Broker Object 

## override functions 

1. we have a default candle stick that all brokers need to convert their candle object to the default one I have to implement a function that all the brokers be able to do that.

## How the broker project should looks like ? 

we should have the broker class as a supper class then we can use delegates and Lazy loading to initialise the end points.
combining factory approach, dependency injection and delegates make the code cleaner and improve the performance.