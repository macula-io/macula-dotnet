namespace Macula.Ucan;

/// <summary>
/// Describes what a service requires to answer one (realm, procedure):
/// open (any identified caller, the default) or UCAN-gated (the caller's
/// token must verify against RequiredIssuer). Mirrors
/// macula_station_link.erl's own policy shape exactly -- `open |
/// {ucan_required, Issuer}` -- where "Issuer" there is the 32-byte Ed25519
/// public key the gate checks the token's signature against, not a DID
/// string.
///
/// Gating happens BEFORE a handler runs -- see
/// Connection.Session.ServeOneCallGatedAsync -- so a rejected caller never
/// reaches business logic, and an accepted caller's handler never sees the
/// raw token either; the policy layer already did the only thing that
/// mattered with it.
///
/// A gated policy also binds the token to the caller presenting it: the
/// token's aud must be that caller's 32-byte node id as lowercase hex, so a
/// token minted for someone else is refused. The serve paths hand a CALL to
/// the policy only once its signature verifies against the caller it names.
/// </summary>
public sealed record Policy(bool Gated, byte[]? RequiredIssuer)
{
    /// <summary>The default, ungated policy: any identified caller may invoke the procedure, no UCAN token needed. Equivalent to Erlang's `open`.</summary>
    public static readonly Policy Open = new(false, null);

    /// <summary>Builds a UCAN-gated policy: a caller must present a token that verifies (signature, exp, nbf) against issuerPublicKey and names that caller as its audience. Equivalent to Erlang's `{ucan_required, issuerPublicKey}`.</summary>
    public static Policy Required(byte[] issuerPublicKey) => new(true, issuerPublicKey);

    /// <summary>
    /// Applies this policy to an inbound CALL's ucanToken and caller,
    /// throwing if the call is NOT authorized to proceed to
    /// lookup/dispatch. An open policy always passes. A gated policy
    /// requires a caller, requires ucanToken to Verify against
    /// RequiredIssuer, and requires the token's aud to equal the caller's
    /// node id as lowercase hex (<c>Convert.ToHexStringLower(caller)</c>),
    /// the same comparison macula_station_link.erl makes.
    /// </summary>
    public void Check(byte[] ucanToken, byte[] caller)
    {
        if (!Gated)
        {
            return;
        }
        if (ucanToken.Length == 0)
        {
            throw new UcanToken.NoTokenException();
        }
        if (caller.Length != 32)
        {
            throw new UcanToken.NoCallerException();
        }
        var payload = UcanToken.Verify(ucanToken, RequiredIssuer!);
        if (payload.Audience != Convert.ToHexStringLower(caller))
        {
            throw new UcanToken.WrongAudienceException();
        }
    }
}
