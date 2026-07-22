Feature: Login
  As a user I can sign in

  @smoke
  Scenario: Successful sign in
    Given a user named Alice
    When they sign in
    Then the dashboard is shown
    And pigs can fly

# "And pigs can fly" is a DELIBERATELY UNBOUND step — no binding matches it (slice 2 records it
# as an `unbound` diagnostic row).
