Feature: Checkout

  Scenario: Place an order
    Given a cart with 3 items
    When the customer checks out
    Then the order "ORD-1" is placed
