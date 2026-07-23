Feature: Purchase journey (E2E)
    A full journey: sign in via the API, browse a product in the UI, then check out.

    Scenario: Sign in, view a product, and place an order
        Given the customer "ada@example.com" is authenticated
        And the product 42 page is open
        When the customer checks out with address "1 King Street"
        Then the order is confirmed
