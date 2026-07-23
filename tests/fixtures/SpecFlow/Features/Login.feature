Feature: Login
  As a user I can sign in

  @smoke
  Scenario: Successful sign in
    Given a user named Alice
    When they sign in
    Then the dashboard is shown
    And pigs can fly

  Scenario: Readiness
    Given the system is ready

# "And pigs can fly" is a DELIBERATELY UNBOUND step — no binding matches it (recorded as an
# `unbound` edge). "Given the system is ready" is DELIBERATELY AMBIGUOUS — both SystemReadyExact
# ("the system is ready") and SystemReadyPattern ("the system is (.*)") match it, so it records two
# binds_to edges with confidence `ambiguous`.
