﻿/*
MIT License

Copyright (c) 2020 Paul Kortekaas

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using CoreWUS;
using CoreWUS.Http;
using Serilog;

namespace WusClient
{
    class Program
    {
        // run from solution folder: dotnet run -p WusClient [-- -r <reference number>]
        static void Main(string[] args)
        {
            // Change for your own certificate, or use filename/password
            string myPKIoThumbprint = Environment.GetEnvironmentVariable("PKIO_THUMB");
            string path = Path.GetFileName(Directory.GetCurrentDirectory());

            string reference = null;
            string instance = "./xbrl/VB-01_bd-rpt-ob-aangifte-2020.xbrl";
            string noAusp = "http://geenausp.nl";
            string scenario = "Happyflow";
            bool preprod = false;

            string baseUrl = "https://cs-bedrijven.procesinfrastructuur.nl";
            string deliveryUrl = "https://cs-bedrijven.procesinfrastructuur.nl/cpl/aanleverservice/1.2";
            string statusUrl = "https://cs-bedrijven.procesinfrastructuur.nl/cpl/statusinformatieservice/1.2";
            string serverCertificateThumbprint = "7C46D36D7D8B5B5CB14FCC6DDBCD551BDB8B1DD0";

            if (preprod)
            {
                baseUrl = "https://preprod-dgp2.procesinfrastructuur.nl";
                deliveryUrl = "https://preprod-dgp2.procesinfrastructuur.nl/wus/2.0/aanleverservice/1.2";
                statusUrl=  "https://preprod-dgp2.procesinfrastructuur.nl/wus/2.0/statusinformatieservice/1.2";
                serverCertificateThumbprint = "519B3FBE5C949B25FF908876905A0163A39DB555";
            }

            // quirk to adjust instance name when
            // running in debugger...
            // Don't want to use an absolute path
            if (path == "WusClient")
            {
                instance = "." + instance;
            }
            if (!File.Exists(instance))
            {
                Console.WriteLine("Unable to find the sample instance document.");
                Environment.Exit(-1);
            }

            // allow command line argument -r <reference number>
            if (args.Length == 2 && args[0] == "-r")
            {
                reference = args[1];
            }

            aanleverRequest request = new aanleverRequest()
            {
                berichtsoort = "Omzetbelasting",
                aanleverkenmerk = preprod ? Guid.NewGuid().ToString("D") : scenario,
                autorisatieAdres = noAusp,
                identiteitBelanghebbende = new identiteitType() {
                    nummer = "001000044B37",
                    type = "Fi"
                },
                rolBelanghebbende = "Bedrijf",
                berichtInhoud = new berichtInhoudType() {
                    mimeType = "test/xml",
                    bestandsnaam = "Omzetbelasting.xbrl",
                    inhoud = File.ReadAllBytes(instance)
                }
            };

            //using (X509Certificate2 cert = new X509Certificate2("filename", "password"))
            using (X509Certificate2 cert = FindCertificate(myPKIoThumbprint))
            {
                try
                {
                    var logLevel = new Serilog.Core.LoggingLevelSwitch();

                    Serilog.Log.Logger = new LoggerConfiguration()
                                .WriteTo.Console()
                                .MinimumLevel.ControlledBy(logLevel)
                                .CreateLogger();

                    logLevel.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

                    IWusHttpClient wusHttpClient = new WusHttpClient(new Uri(baseUrl), cert, serverCertificateThumbprint);
                    IWusXmlDSig xmlDSig = new WusXmlDSig();

                    Logger.Info("Startup");
                    WusProcessor wp = new WusProcessor(wusHttpClient, xmlDSig, cert);

                    if (string.IsNullOrEmpty(reference))
                    {
                        Logger.Info("-------------------- Deliver --------------------");
                        aanleverResponse response = wp.Deliver(request, new Uri(deliveryUrl));
                        reference = response.kenmerk;
                        Logger.Info($"Aanleverkenmerk: {response.aanleverkenmerk}");
                        Logger.Info($"Kenmerk: {reference}");
                    }

                    Logger.Info("------------------- New Status ------------------");
                    getNieuweStatussenProcesRequest statusNewRequest = new getNieuweStatussenProcesRequest();
                    statusNewRequest.kenmerk = reference;
                    statusNewRequest.autorisatieAdres = noAusp;
                    IEnumerable<StatusResultaat> statusResponse = wp.NewStatusProcess(statusNewRequest, new Uri(statusUrl));
                    foreach (StatusResultaat statusResultaat in statusResponse)
                    {
                        Logger.Info($"Status: {statusResultaat.statuscode} - {statusResultaat.statusomschrijving}");
                    }

                    Logger.Info("------------------- All Status ------------------");
                    getStatussenProcesRequest statusAllRequest = new getStatussenProcesRequest();
                    statusAllRequest.kenmerk = reference;
                    statusAllRequest.autorisatieAdres = noAusp;
                    statusResponse = wp.AllStatusProcess(statusAllRequest, new Uri(statusUrl));
                    foreach (StatusResultaat statusResultaat in statusResponse)
                    {
                        Logger.Info($"Status: {statusResultaat.statuscode} - {statusResultaat.statusomschrijving}");
                    }
                }
                catch (WusException e)
                {
                    Logger.Error(e, $"WusException: {e.WusCode} - {e.WusMessage}");
                }
                catch (SoapException e)
                {
                    Logger.Error($"SoapException: {e.Code} - {e.Message}");
                }
                catch (HttpException e)
                {
                    Logger.Error($"HttpException: {e.Code} - {e.Message}");
                }
                catch (Exception e)
                {
                    Logger.Error($"Exception: {e.Message}");
                }
                finally
                {
                    Logger.Info("Shutdown");
                }
            }

        }

        public static X509Certificate2 FindCertificate(string thumbprint)
        {
            using (X509Store certStore = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                certStore.Open(OpenFlags.ReadOnly);

                X509Certificate2Collection certCollection = certStore.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    thumbprint,
                    false);

                if (certCollection.Count > 0)
                {
                    return certCollection[0];
                }
            }
            return null;
        }

        /*
        private static void StoreCertificate(X509Certificate2 certificate)
        {
            using (X509Store certStore = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                certStore.Open(OpenFlags.ReadWrite);
                certStore.Add(certificate);
            }
        }
        */
    }
}