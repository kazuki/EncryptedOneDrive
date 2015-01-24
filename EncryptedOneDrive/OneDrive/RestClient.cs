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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using EncryptedOneDrive.Security;

namespace EncryptedOneDrive.OneDrive
{
    class RestClient : IRestClient
    {
        const string BASE_URL = "https://apis.live.net/v5.0/";
        public const string RootID = "me/skydrive";
        const string QUOTA_URL = BASE_URL + RootID + "/quota";
        static readonly Dictionary<Type, DataContractJsonSerializer> SerDeMap = new Dictionary<Type, DataContractJsonSerializer> ();

        static RestClient()
        {
            Type[] types = new Type[] {
                typeof (UploadResult),
                typeof (ObjectProperty),
                typeof (FileListResult),
                typeof (QuotaResponse)
            };
            foreach (var t in types)
                SerDeMap.Add (t, new DataContractJsonSerializer (t));
        }

        public RestClient (LiveConnectClient oauthClient)
        {
            OAuthClient = oauthClient;
        }

        public LiveConnectClient OAuthClient { get; private set; }

        /// <returns>アップロードしたファイルのID</returns>
        public string Upload (string folderId, string name, Stream strm)
        {
            var req = CreateRequest (BASE_URL + folderId + "/files/" + Uri.EscapeDataString (name), "PUT");
            using (var writeStrm = req.GetRequestStream ()) {
                strm.CopyTo (writeStrm);
            }
            return GetJsonResponse<UploadResult> (req).ID;
        }

        public Stream Download (string fileId)
        {
            var req = CreateRequest (BASE_URL + fileId + "/content");
            var res = req.GetResponse ();
            return res.GetResponseStream ();
        }

        public ObjectProperty GetProperty (string objectId)
        {
            var req = CreateRequest (BASE_URL + objectId);
            return GetJsonResponse<ObjectProperty> (req);
        }

        public ObjectProperty[] GetChildren (string folderId)
        {
            var req = CreateRequest (BASE_URL + folderId + "/files");
            return GetJsonResponse<FileListResult> (req).Data;
        }

        public ObjectProperty CreateDirectory (string folderId, string name)
        {
            var req = CreateRequest (BASE_URL + folderId, "POST");
            req.ContentType = "application/json";
            var reqBody = string.Format ("{{\"name\": \"{0}\"}}", Uri.EscapeDataString (name));
            byte[] reqBodyRaw = System.Text.Encoding.UTF8.GetBytes (reqBody);
            using (var strm = req.GetRequestStream ()) {
                strm.Write (reqBodyRaw, 0, reqBodyRaw.Length);
            }
            return GetJsonResponse<ObjectProperty> (req);
        }

        public void Delete (string objectId)
        {
            var req = CreateRequest (BASE_URL + objectId, "DELETE");
            using (var res = (HttpWebResponse)req.GetResponse ()) {
                if (res.StatusCode != HttpStatusCode.NoContent)
                    throw new IOException ();
                return;
            }
        }

        public void GetQuota (out long quota, out long available)
        {
            var data = GetJsonResponse<QuotaResponse> (CreateRequest (QUOTA_URL));
            quota = data.Quota;
            available = data.Available;
        }

        HttpWebRequest CreateRequest (string url, string method = "GET")
        {
            bool authInHeader = (method == "POST");
            if (!authInHeader)
                url += (url.Contains ("?") ? "&" : "?") + "access_token=" + Uri.EscapeDataString (OAuthClient.AccessToken);
            var req = (HttpWebRequest)WebRequest.Create (url);
            req.Method = method;
            if (authInHeader)
                req.Headers.Add ("Authorization", "Bearer " + OAuthClient.AccessToken);
            return req;
        }

        static T GetJsonResponse<T> (WebRequest req)
        {
            var serde = SerDeMap[typeof (T)];
            using (var res = req.GetResponse ()) {
                lock (serde) {
                    return (T)serde.ReadObject (res.GetResponseStream ());
                }
            }
        }
    }
}
