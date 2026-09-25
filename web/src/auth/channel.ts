/**
 * The device id the management site acts under, on every write that names a
 * device: registration, correction, certificate issue and reprint, outcomes
 * and BRN block requests.
 *
 * The API decides the channel from the token, not from this value: a token
 * issued to the `ncbrs-web` client is the management site, and it may act only
 * as `ncbrs-web` -- never as a tablet, because a browser holds no device key.
 * So this must equal both the Keycloak client the site signs in with and the
 * API's `DeviceEnrolment:WebClientId`, or every write is refused as the wrong
 * channel.
 *
 * One constant rather than one per screen: five copies were five places for
 * the rule to be broken by an edit to just one of them.
 */
export const WebChannelDeviceId = 'ncbrs-web'
