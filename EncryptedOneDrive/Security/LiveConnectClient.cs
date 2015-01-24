// Copyright (C) 2014  Kazuki Oikawa
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Net;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace EncryptedOneDrive.Security
{
    public class LiveConnectClient
    {
        const string LiveOAuthEndPoint = "https://login.live.com/oauth20_token.srf";
        const string DesktopAppRedirectURL = "https://login.live.com/oauth20_desktop.srf";
        static readonly DataContractJsonSerializer TokenResponseSerDe = new DataContractJsonSerializer (typeof (TokenResponse));

        public event EventHandler Authorized;
        public event EventHandler TokenUpdated;

        public LiveConnectClient (string appId, string[] scopes)
        {
            AppID = Uri.EscapeDataString (appId);
            EncodedScopes = Uri.EscapeDataString (string.Join (" ", scopes));
            Code = AccessToken = RefreshToken = null;
            LoginUrl = string.Format ("https://login.live.com/oauth20_authorize.srf?client_id={0}&scope={1}&response_type=code&redirect_uri={2}",
                AppID, EncodedScopes, Uri.EscapeDataString(DesktopAppRedirectURL));
        }

        string Code { get; set; }

        public string LoginUrl { get; private set; }
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public DateTime Expires { get; set; }
        public string UserID { get; set; }

        string AppID { get; set; }
        string EncodedScopes { get; set; }

        public void Authorize(string code)
        {
            this.Code = code;
            this.AccessToken = this.RefreshToken = this.UserID = null;
            this.Expires = DateTime.MinValue;
            try {
                GetTokenInternal (GrantType.AuthorizationCode);
            } finally {
                if (Authorized != null)
                    Authorized (this, EventArgs.Empty);
            }
        }

        public void RefreshAccessToken ()
        {
            if (string.IsNullOrEmpty(RefreshToken))
                throw new InvalidOperationException ();
            GetTokenInternal (GrantType.RefreshToken);
        }

        public bool ValidateAccessToken ()
        {
            if (string.IsNullOrEmpty (AccessToken))
                return false;
            var req = (HttpWebRequest)WebRequest.Create (string.Format (
                              "https://apis.live.net/v5.0/me?access_token={0}",
                              Uri.EscapeDataString (AccessToken)));
            try {
                using (var res = (HttpWebResponse)req.GetResponse ()) {
                    if (res.StatusCode == HttpStatusCode.OK)
                        return true;
                    return false;
                }
            } catch {
                return false;
            }
        }

        void GetTokenInternal (GrantType grantType)
        {
            var req = (HttpWebRequest)WebRequest.Create (LiveOAuthEndPoint);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            string body = string.Format ("client_id={0}&redirect_uri={1}",
                              AppID, Uri.EscapeDataString (DesktopAppRedirectURL));
            if (grantType == GrantType.AuthorizationCode) {
                body += string.Format ("&code={0}&grant_type=authorization_code",
                    Uri.EscapeDataString (this.Code));
            } else {
                body += string.Format ("&refresh_token={0}&grant_type=refresh_token",
                    Uri.EscapeDataString (this.RefreshToken));
            }
            byte[] raw = System.Text.Encoding.ASCII.GetBytes (body);
            req.ContentLength = raw.Length;
            using (var strm = req.GetRequestStream ()) {
                strm.Write (raw, 0, raw.Length);
            }
            using (var res = (HttpWebResponse)req.GetResponse ()) {
                lock (TokenResponseSerDe) {
                    var obj = (TokenResponse)TokenResponseSerDe.ReadObject (res.GetResponseStream ());
                    AccessToken = obj.AccessToken;
                    RefreshToken = obj.RefreshToken;
                    Expires = DateTime.UtcNow.AddSeconds (obj.ExpiresIn);
                    UserID = obj.UserID;
                }
            }
            if (TokenUpdated != null)
                TokenUpdated (this, EventArgs.Empty);
        }

        enum GrantType
        {
            AuthorizationCode,
            RefreshToken
        }

        [DataContract]
        class TokenResponse
        {
            [DataMember(Name = "token_type")]
            public string TokenType { get; set; }

            [DataMember(Name = "expires_in")]
            public int ExpiresIn { get; set; }

            [DataMember(Name = "scope")]
            public string Scope { get; set; }

            [DataMember(Name = "access_token")]
            public string AccessToken { get; set; }

            [DataMember(Name = "refresh_token")]
            public string RefreshToken { get; set; }

            [DataMember(Name = "user_id")]
            public string UserID { get; set; }
        }
    }
}
