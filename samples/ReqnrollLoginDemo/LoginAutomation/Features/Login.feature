Feature: Login
  As a registered user I want to sign in

  Scenario: Successful sign in
    Given a registered user "alice"
    When she signs in with password "hunter2"
    Then she sees the dashboard

  Scenario: Locked out after failures
    Given a registered user "bob"
    When he signs in with the wrong password 3 times
    Then the account is locked
    And an unbound narrative step with no binding
