Feature: Checkout UI
    Signing in and placing an order through the web UI.

    Scenario: Place an order as a signed-in customer
        Given the customer "ada@example.com" is signed in
        When they check out with shipping address "1 King Street"
        Then the order is placed
