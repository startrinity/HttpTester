using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;

namespace StarTrinity.HttpTester
{
    public class MainViewModel: BaseNotify
    {
        #region HTTP request params
        string _url;
        public string Url
        {
            get
            {
                return _url ?? (_url = Properties.Settings.Default.Url);
            }
            set
            {
                Properties.Settings.Default.Url = _url = value;
                Properties.Settings.Default.Save();
            }
        }

        string _digestUserName;
        public string DigestUserName
        {
            get
            {
                return _digestUserName ?? (_digestUserName = Properties.Settings.Default.DigestUserName);
            }
            set
            {
                Properties.Settings.Default.DigestUserName = _digestUserName = value;
                Properties.Settings.Default.Save();
            }
        }

        string _digestPassword;
        public string DigestPassword
        {
            get
            {
                return _digestPassword ?? (_digestPassword = Properties.Settings.Default.DigestPassword);
            }
            set
            {
                Properties.Settings.Default.DigestPassword = _digestPassword = value;
                Properties.Settings.Default.Save();
            }
        }

        string _postString;
        public string PostString
        {
            get
            {
                return _postString ?? (_postString = Properties.Settings.Default.PostString);
            }
            set
            {
                Properties.Settings.Default.PostString = _postString = value;
                Properties.Settings.Default.Save();
            }
        }

        string _status;
        public string Status { get { return _status; } set { _status = value; RaisePropertyChanged(() => Status); } }

        public enum HttpMethods
        {
            GET,
            POST
        }
        HttpMethods? _httpMethod;
        public HttpMethods HttpMethod
        {
            get
            {
                if (_httpMethod.HasValue) return _httpMethod.Value;
                var httpMethodStr = Properties.Settings.Default.HttpMethod;
                if (String.IsNullOrEmpty(httpMethodStr)) httpMethodStr = HttpMethods.GET.ToString();
                _httpMethod = (HttpMethods)Enum.Parse(typeof(HttpMethods), httpMethodStr);
                return _httpMethod.Value;
            }
            set
            {
                _httpMethod = value;
                Properties.Settings.Default.HttpMethod = value.ToString();
                Properties.Settings.Default.Save();
            }
        }
        public IEnumerable<HttpMethods> HttpMethodsList
        {
            get
            {
                return new[] { HttpMethods.GET, HttpMethods.POST };
            }
        }
        #endregion
        
        public ICommand SendRequest
        {
            get
            {
                return new DelegateCommand(() =>
                {
                    SendRequestProcedure(true);
                });
            }
        }
        void SendRequestProcedure(bool setStatusField)
        {
            try
            {
                var wc = new WebClient();

                if (!String.IsNullOrEmpty(DigestUserName) && !String.IsNullOrEmpty(DigestPassword))
                {
                    wc.Credentials = new NetworkCredential(DigestUserName, DigestPassword);
                }

                var sw = Stopwatch.StartNew();

                switch (HttpMethod)
                {
                    case HttpMethods.POST:
                        wc.UploadStringAsync(new Uri(Url), PostString);
                        if (setStatusField) Status = "requesting...";
                        wc.UploadStringCompleted += (s, ea) => OnRequestCompleted(sw, ea, setStatusField);
                        break;
                    case HttpMethods.GET:
                        wc.DownloadStringAsync(new Uri(Url));
                        if (setStatusField) Status = "requesting...";
                        wc.DownloadStringCompleted += (s, ea) => OnRequestCompleted(sw, ea, setStatusField);
                        break;
                    default: throw new NotImplementedException("invalid HTTP method");
                }
                _statistics.OnRequestSent();
            }
            catch (Exception exc)
            {
                OnError(exc);
            }
        }
        void OnRequestCompleted(Stopwatch sw, AsyncCompletedEventArgs ea, bool setStatusField)
        {
            string result = "";
            if (setStatusField)
            {
                if (ea.Error != null)
                {
                    result = "error: " + ea.Error.Message;
                }
                else
                {
                    if (ea is UploadStringCompletedEventArgs) result = String.Format("response = '{0}'", ((UploadStringCompletedEventArgs)ea).Result);
                    else if (ea is DownloadStringCompletedEventArgs) result = String.Format("response = '{0}'", ((DownloadStringCompletedEventArgs)ea).Result);
                }
            }

            sw.Stop();
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (setStatusField) Status = String.Format("completed in {0:0.0}ms. {1}", sw.Elapsed.TotalMilliseconds, result);
                _statistics.OnRequestCompleted(ea.Error == null, sw.Elapsed.TotalMilliseconds);
            }));
        }

        #region timer
        static readonly DateTime _timeStartedUtc = DateTime.UtcNow;
        static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        public static DateTime DateTimeNowNonInterruptedUtc { get { return _timeStartedUtc + _stopwatch.Elapsed; } }

        double? _timerIntervalMs;
        public double TimerIntevalMs
        {
            get
            {
                return _timerIntervalMs ?? (_timerIntervalMs = Properties.Settings.Default.TimerIntervalMs).Value;
            }
            set
            {
                _timerIntervalMs = value;
                Properties.Settings.Default.TimerIntervalMs = value;
                Properties.Settings.Default.Save();

            }
        }

        DateTime? NextScheduledRequestTimeUtc { get; set; }
        bool _sendRequestsOnTimer;
        public bool SendRequestsOnTimer
        {
            get
            {
                return _sendRequestsOnTimer;
            }
            set
            {
                _sendRequestsOnTimer = value;
                if (value) NextScheduledRequestTimeUtc = DateTimeNowNonInterruptedUtc;
                else NextScheduledRequestTimeUtc = null;
                RaisePropertyChanged(() => SendRequestsOnTimer);
            }
        }

        private Timer _timer;
        bool _executingTimerProcedureNow = false;
        private void TimerOnElapsed(object sender, ElapsedEventArgs elapsedEventArgs)
        {
            if (_executingTimerProcedureNow) return;
            _executingTimerProcedureNow = true;
            try
            {
                if (_sendRequestsOnTimer)
                {
                    while (DateTimeNowNonInterruptedUtc >= NextScheduledRequestTimeUtc)
                    {
                        SendRequestProcedure(false);
                        NextScheduledRequestTimeUtc = NextScheduledRequestTimeUtc.Value.AddMilliseconds(TimerIntevalMs);
                    }
                }

                ReportsStatistics = _statistics.ToString();
            }
            catch (Exception exc)
            {
                OnError(exc);
            }
            _executingTimerProcedureNow = false;
        }
        #endregion

        #region reports/statistics
        class Statistics
        {
            int SentRequests;
            int SuceededRequests;
            int FailedRequests;

            double DelaysSumMs;
            int DelaysCount;
            double MaxDelayMs;
            DateTime? MaxDelayAt_utc;

            public override string ToString()
            {
                var r = new StringBuilder();
                r.AppendFormat("sent requests: {0}\r\n", SentRequests);
                r.AppendFormat("successful requests: {0}\r\n", SuceededRequests);
                r.AppendFormat("failed requests: {0}\r\n", FailedRequests);
                if (DelaysCount != 0)
                {
                    r.AppendFormat("max delay: {0:0.0}ms", MaxDelayMs);
                    if (MaxDelayAt_utc != null) r.AppendFormat(" at {0:yyyy-MM-dd HH:mm:ss}UTC", MaxDelayAt_utc.Value);
                    r.Append("\r\n");
                    r.AppendFormat("average delay: {0:0.0}ms\r\n", DelaysSumMs / DelaysCount);
                }
                return r.ToString();
            }
            public void OnRequestSent()
            {
                SentRequests = SentRequests + 1;
            }
            public void OnRequestCompleted(bool suceeded, double delayMs)
            {
                if (suceeded) SuceededRequests = SuceededRequests + 1;
                else FailedRequests = FailedRequests + 1;

                DelaysSumMs += delayMs;
                DelaysCount++;
                if (delayMs > MaxDelayMs)
                {
                    MaxDelayMs = delayMs;
                    MaxDelayAt_utc = DateTime.UtcNow;
                }
            }
        }
        Statistics _statistics = new Statistics();

        string _reportsStatistics;
        public string ReportsStatistics
        {
            get
            {
                return _reportsStatistics;
            }
            set
            {
                _reportsStatistics = value;
                RaisePropertyChanged(() => ReportsStatistics);
            }
        }
        public ICommand ResetStatistics
        {
            get
            {
                return new DelegateCommand(() =>
                {
                    _statistics = new Statistics();
                    ReportsStatistics = _statistics.ToString();
                });
            }
        }
        #endregion

        public MainViewModel()
        {
            _timer = new Timer(10);
            _timer.Enabled = true;
            _timer.Start();
            _timer.Elapsed += TimerOnElapsed;
        }
        public void Dispose()
        {
            DisposeTimer();
        }
        void DisposeTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }

        }
        void OnError(Exception exc)
        {
            _sendRequestsOnTimer = false;
            App.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                SendRequestsOnTimer = false;
                MessageBox.Show("Error: " + exc.ToString());
            }));
        }
    }
}
