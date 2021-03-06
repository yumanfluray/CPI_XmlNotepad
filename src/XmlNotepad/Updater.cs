﻿using System;
using System.Text;
using System.Xml;
using System.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using System.Net;
using System.IO;
using System.Collections.Generic;

namespace XmlNotepad
{
    class Updater : IDisposable
    {
        Settings settings;
        DateTime lastCheck = DateTime.MinValue;
        TimeSpan updateFrequency = TimeSpan.MaxValue;
        Uri updateUri;
        DelayedActions delayedActions = new DelayedActions();
        const string RetryAction = "Retry";
        const int DefaultDelay = 5000;
        string download;
        string installer;
        string history;
        string title;
        string version;
        bool enabled = true;
        WebRequest req;
        bool disposed;
        int retryCount;
        const int MaxRetries = 10;
        TimeSpan MinimumUpdateFrequency = TimeSpan.FromSeconds(5);

        public event EventHandler<bool> UpdateRequired;

        public Updater(Settings s)
        {
            this.settings = s;
            s["LastUpdateCheck"] = lastCheck;
            s["UpdateFrequency"] = updateFrequency;
            s["UpdateLocation"] = "";
            s["UpdateEnabled"] = enabled;
            s.Changed += new SettingsEventHandler(OnSettingChanged);
            StartTimer();
        }

        void StartTimer(int milliseconds = DefaultDelay)
        {
            if (this.enabled && !this.disposed)
            {
                this.delayedActions.StartDelayedAction(RetryAction, OnTimerTick, TimeSpan.FromMilliseconds(milliseconds));
            }
        }

        void StopTimer()
        {
            this.delayedActions.CancelDelayedAction(RetryAction);
        }

        public string DownloadPage { get { return this.download; } }
        public string Title { get { return this.title; } set { this.title = value; } }
        public string Version { get { return this.version; } set { this.version = value; } }
        public Uri UpdateLocation { get { return this.updateUri; } }
        public string InstallerLocation { get { return this.installer; } }
        public string InstallerHistory { get { return this.history; } }

        void OnSettingChanged(object sender, string name)
        {
            switch (name)
            {
                case "LastUpdateCheck":
                    this.lastCheck = (DateTime)settings["LastUpdateCheck"];
                    break;
                case "UpdateFrequency":
                    SetUpdateFrequency((TimeSpan)settings["UpdateFrequency"]);
                    break;
                case "UpdateLocation":
                    SetUpdateLocation((string)settings["UpdateLocation"]);
                    break;
                case "UpdateEnabled":
                    SetEnabled((bool)settings["UpdateEnabled"]);
                    break;
            }
        }

        void SetEnabled(bool e)
        {
            if (this.enabled != e)
            {
                this.enabled = e;
                if (e && !this.disposed)
                {
                    StartTimer();
                }
                else
                {
                    StopTimer();
                }
            }
        }

        void SetUpdateFrequency(TimeSpan ts)
        {
            if (ts == TimeSpan.MaxValue || ts < MinimumUpdateFrequency)
            {
                ts = MinimumUpdateFrequency;
            }
            this.updateFrequency = ts;
            TimeSpan f = (TimeSpan)settings["UpdateFrequency"];
            if (f != ts)
            {
                settings["UpdateFrequency"] = ts;
            }
            StartTimer();
        }

        void SetUpdateLocation(string location)
        {
            if (string.IsNullOrEmpty(location)) return;
            Uri uri = new Uri(location);
            if (uri != this.updateUri)
            {
                this.retryCount = 0;
                this.updateUri = uri;
                if ((string)settings["UpdateLocation"] != location)
                {
                    settings["UpdateLocation"] = location;
                }
                // Location has just changed, so we need to download the new update information.
                StartTimer();
            }
        }

        public void OnUserChange(string oldUri)
        {
            if ((string)settings["UpdateLocation"] != oldUri)
            {
                // then this user changed the location, so we need to ping the new
                // location right away.
                this.retryCount = 0;
                this.lastCheck = DateTime.MinValue;
                StartTimer();
            }
        }

        void OnTimerTick()
        {
            if (this.updateUri == null)
            {
                Bootstrap();
            }
            else if (this.lastCheck == DateTime.MinValue ||
              this.updateFrequency == TimeSpan.MaxValue ||
              this.lastCheck + this.updateFrequency < DateTime.Now)
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(CheckForUpdate));
            }
        }

        bool busy;

        void CheckForUpdate(object state)
        {
            if (busy)
            {
                return;
            }
            busy = true;
            if (this.updateUri != null)
            {
                try
                {
                    // assume success in this request so we don't create DOS attacks on the server!
                    this.lastCheck = DateTime.Now;
                    settings["LastUpdateCheck"] = this.lastCheck;

                    // now check
                    WebRequest wr = WebRequest.Create(this.updateUri);
                    this.req = wr;
                    wr.Credentials = CredentialCache.DefaultCredentials;
                    wr.Proxy = WebRequest.DefaultWebProxy;
                    WebResponse r = wr.GetResponse();
                    XmlDocument doc = null;
                    using (Stream s = r.GetResponseStream())
                    {
                        doc = new XmlDocument();
                        doc.Load(s);
                    }
                    if (!this.disposed)
                    {
                        ProcessUpdate(doc);
                    }

                }
                catch (Exception)
                {
                    // try again in a bit...
                    this.retryCount++;
                    if (this.retryCount < MaxRetries)
                    {
                        StartTimer();
                    }
                }
                finally
                {
                    this.req = null;
                }
            }
            busy = false;
        }

        void Bootstrap()
        {
            // See if we can find a local copy of updates.xml so that we can bootstrap the
            // location from there.
            Uri baseUri = new Uri(this.GetType().Assembly.Location);
            Uri resolved = new Uri(baseUri, "updates.xml");
            string file = resolved.LocalPath;
            if (File.Exists(file))
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(file);
                    ProcessUpdate(doc);
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                }
            }
        }

        void ProcessUpdate(XmlDocument doc)
        {
            Version v = GetType().Assembly.GetName().Version;
            string ver = v.ToString();

            XmlElement loc = doc.SelectSingleNode("updates/application/location") as XmlElement;
            if (loc != null)
            {
                try
                {
                    Uri uri = new Uri(loc.InnerText);
                    if (uri != this.updateUri)
                    {
                        string location = uri.IsFile ? uri.LocalPath : uri.AbsoluteUri;
                        SetUpdateLocation(location);
                        return; // page has been moved - start over!
                    }
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.Message);
                }
            }

            XmlElement de = doc.SelectSingleNode("updates/application/download") as XmlElement;
            if (de != null)
            {
                this.download = de.InnerText;
            }

            XmlElement ie = doc.SelectSingleNode("updates/application/installer") as XmlElement;
            if (ie != null)
            {
                this.installer = ie.InnerText;
            }

            XmlElement ih = doc.SelectSingleNode("updates/application/history") as XmlElement;
            if (ih != null)
            {
                this.history = ih.InnerText;
            }

            XmlElement f = doc.SelectSingleNode("updates/application/frequency") as XmlElement;
            if (f != null)
            {
                try
                {
                    SetUpdateFrequency(TimeSpan.Parse(f.InnerText));
                }
                catch (Exception ex)
                {
                    Trace.WriteLine(ex.Message);
                }
            }
            bool newVersion = false;
            foreach (XmlElement e in doc.SelectNodes("updates/version"))
            {
                string n = e.GetAttribute("number");
                if (!string.IsNullOrEmpty(n))
                {
                    Version v2 = new Version(n);
                    if (v2 > v)
                    {
                        // new version is available!
                        this.version = n;
                        newVersion = true;
                        break;
                    }
                }
            }
            if (this.UpdateRequired != null)
            {
                this.UpdateRequired(this, newVersion);
            }
        }

        public void Dispose()
        {
            this.disposed = true;
            StopTimer();
            WebRequest r = this.req;
            if (r != null)
            {
                try
                {
                    r.Abort();
                }
                catch
                {
                }
            }
        }

        internal void CheckNow()
        {
            StopTimer();
            if (this.updateUri != null)
            {
                ThreadPool.QueueUserWorkItem(new WaitCallback(CheckForUpdate));
            }
        }
    }
}
