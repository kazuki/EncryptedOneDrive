using System;

namespace EncryptedOneDrive
{
    public static class LiveOAuth2
    {
        const string AuthorizationEndPoint = "https://login.live.com/oauth20_authorize.srf";

        public static string GetUrl(string clientId)
        {
            return string.Format ("{0}?client_id={1}&scope=wl.signin%20wl.basic%20wl.contacts_skydrive%20wl.skydrive_update&response_type=token&redirect_uri=https%3A%2F%2Flogin.live.com%2Foauth20_desktop.srf",
                AuthorizationEndPoint, clientId);
        }
    }
}
