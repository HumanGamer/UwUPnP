﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace UwUPnP
{
	//http://www.upnp-hacks.org/igd.html

	//Originally based on a port of waifuPnP. Mostly rewritten.

	internal class Gateway
	{
		private readonly IPAddress client;

		private readonly string serviceType = null;
		private readonly string controlURL = null;

		public Gateway(IPAddress ip, string data)
		{
			client = ip;

			string location = GetLocation(data);

			(serviceType, controlURL) = GetInfo(location);
		}

		private static string GetLocation(string data)
		{
			var lines = data.Split('\n').Select(l=>l.Trim()).Where(l=>l.Length>0);

			foreach(string line in lines)
			{
				if(line.StartsWith("HTTP/1.") || line.StartsWith("NOTIFY *"))
					continue;

				int colonIndex = line.IndexOf(':');

				if(colonIndex<0)
					continue;

				string name = line[..colonIndex];
				string val = line.Length >= name.Length ? line[(colonIndex + 1)..].Trim() : null;

				if(name.ToLowerInvariant() == "location")
				{
					if(val.IndexOf('/', 7) == -1)// finds the first slash after http://
					{
						throw new Exception("Unsupported Gateway");
					}
					return val;
				}
			}
			throw new Exception("Unsupported Gateway");
		}

		private static (string serviceType, string controlURL) GetInfo(string location)
		{
			XDocument doc = XDocument.Load(location);
			var services =  doc.Descendants().Where(d => d.Name.LocalName == "service");

			(string serviceType,string controlURL) ret = (null,null);

			foreach(XElement service in services)
			{
				string serviceType = null;
				string controlURL = null;

				foreach(var node in service.Nodes())
				{
					if(node is not XElement ele || ele.FirstNode is not XText n)
					{
						continue;
					}

					switch(ele.Name.LocalName.Trim().ToLowerInvariant())
					{
						case "servicetype": serviceType = n.Value.Trim(); break;
						case "controlurl": controlURL = n.Value.Trim(); break;
					}
				}

				if(serviceType is not null && controlURL is not null)
				{
					if(serviceType.Contains(":wanipconnection:", StringComparison.InvariantCultureIgnoreCase) || serviceType.Contains(":wanpppconnection:", StringComparison.InvariantCultureIgnoreCase))
					{
						ret.serviceType = serviceType;
						ret.controlURL = controlURL;
					}
				}
			}

			if(ret.controlURL is null)
			{
				throw new Exception("Unsupported Gateway");
			}

			if(!ret.controlURL.StartsWith('/'))
			{
				ret.controlURL = "/" + ret.controlURL;
			}

			int slash = location.IndexOf('/', 7); // finds the first slash after http://

			ret.controlURL = location[0..slash] + ret.controlURL;

			return ret;
		}

		private static string BuildArgString(IEnumerable<(string Key, object Value)> args) => string.Concat(args.Select(a => $"<{a.Key}>{a.Value}</{a.Key}>"));

		private Dictionary<string, string> RunCommand(string action, params (string Key, object Value)[] args)
		{
			string requestData = GetSoapRequest(action, args);

			HttpWebRequest request = SendRequest(action, requestData);

			return GetResponse(request);
		}

		private string GetSoapRequest(string action, (string Key, object Value)[] args) => string.Concat
		(
			"<?xml version=\"1.0\"?>\n",
			"<SOAP-ENV:Envelope xmlns:SOAP-ENV=\"http://schemas.xmlsoap.org/soap/envelope/\" SOAP-ENV:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">",
				"<SOAP-ENV:Body>",
					$"<m:{action} xmlns:m=\"{serviceType}\">",
						string.Concat(args.Select(a => $"<{a.Key}>{a.Value}</{a.Key}>")),
					$"</m:{action}>",
				"</SOAP-ENV:Body>",
			"</SOAP-ENV:Envelope>"
		);

		private HttpWebRequest SendRequest(string action, string requestData)
		{
			byte[] data = Encoding.ASCII.GetBytes(requestData);

			HttpWebRequest request = (HttpWebRequest)WebRequest.Create(controlURL);

			request.Method = "POST";
			request.ContentType = "text/xml";
			request.ContentLength = data.Length;
			request.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");

			using Stream requestStream = request.GetRequestStream();
			requestStream.Write(data);

			return request;
		}

		private static Dictionary<string, string> GetResponse(HttpWebRequest request)
		{
			using HttpWebResponse response = (HttpWebResponse)request.GetResponse();
			if(response.StatusCode != HttpStatusCode.OK)
			{
				return null;
			}

			Dictionary<string, string> ret = new Dictionary<string, string>();

			XDocument doc = XDocument.Load(response.GetResponseStream());

			if(doc is null)
			{
				throw new NullReferenceException("XML Document is null");
			}

			foreach(XNode node in doc.DescendantNodes())
			{
				if(node is XElement ele && ele.FirstNode is XText txt)
				{
					ret[ele.Name.LocalName] = txt.Value;
				}
			}

			if(ret.TryGetValue("errorCode", out string errorCode))
			{
				throw new Exception(errorCode);
			}

			return ret;
		}

		public IPAddress LocalIP => client;

		public IPAddress ExternalIP => RunCommand("GetExternalIPAddress").TryGetValue("NewExternalIPAddress", out string ret) ? IPAddress.Parse(ret) : null;

		public void Open(Protocol protocol, ushort port, string description) => RunCommand("AddPortMapping",
			("NewRemoteHost", ""),
			("NewProtocol", protocol),
			("NewExternalPort", port),
			("NewInternalClient", client),
			("NewInternalPort", port),
			("NewEnabled", 1),
			("NewPortMappingDescription", description),
			("NewLeaseDuration", 0)
		);

		public void Close(Protocol protocol, ushort port) => RunCommand("DeletePortMapping",
			("NewRemoteHost", ""),
			("NewProtocol", protocol),
			("NewExternalPort", port)
		);

		public bool IsMapped(Protocol protocol, ushort port) => RunCommand("GetSpecificPortMappingEntry",
			("NewRemoteHost", ""),
			("NewProtocol", protocol),
			("NewExternalPort", port)
		).ContainsKey("NewInternalPort");
	}
}