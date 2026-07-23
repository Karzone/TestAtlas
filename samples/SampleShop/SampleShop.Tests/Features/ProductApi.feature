Feature: Product API
    Browsing the catalogue and cart through the REST API.

    Scenario: List available products
        Given the shop API is available
        When a request for the product list is made
        Then the product list is returned

    Scenario: Add a product to the cart
        Given the shop API is available
        When product 42 is added to the cart with quantity 2
        Then the cart contains 1 line item
