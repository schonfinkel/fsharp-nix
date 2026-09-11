namespace App.Views

open App.Domain

type LoginModel =
    { Email: string
      ReturnUrl: string
      Error: string option }

type TwoFactorModel =
    { ReturnUrl: string
      DevelopmentKey: string option
      Error: string option }

type EnrollmentModel =
    { UserName: string
      Email: string
      SharedKey: string option
      AuthenticatorUri: string option
      RecoveryCodesLeft: int
      IsEnabled: bool
      Error: string option }

type AdminFormState =
    { Enabled: bool
      EffectiveAt: string
      Error: string option }

type AdminFeatureModel =
    { Schedule: FeatureSchedule
      Form: AdminFormState }
