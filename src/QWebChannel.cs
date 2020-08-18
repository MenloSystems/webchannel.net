/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2016 - 2018, Menlo Systems GmbH
 * Copyright (C) 2016 The Qt Company Ltd.
 * Copyright (C) 2016 Klarälvdalens Datakonsult AB, a KDAB Group company
 * License: Dual-licensed under LGPLv3 and GPLv2+
 */

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


namespace QWebChannel
{
    enum QWebChannelMessageTypes {
        Signal = 1,
        PropertyUpdate = 2,
        Init = 3,
        Idle = 4,
        Debug = 5,
        InvokeMethod = 6,
        ConnectToSignal = 7,
        DisconnectFromSignal = 8,
        SetProperty = 9,
        Response = 10,
    }

    public class QWebChannel
    {
        QWebChannel channel;
        IWebChannelTransport transport;
        Action<QWebChannel> initCallback = null;
        TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
        public Task IsConnected { get { return connected.Task; } }
        public event EventHandler OnConnected;

        public QWebChannel(IWebChannelTransport transport) : this(transport, null)
        {
        }

        public QWebChannel(IWebChannelTransport transport, Action<QWebChannel> initCallback)
        {
            this.channel = this;
            this.transport = transport;
            this.transport.OnMessage += this.OnMessage;
            this.initCallback = initCallback;

            channel.exec(new { type = (int) QWebChannelMessageTypes.Init }, new Action<JToken>(ConnectionMade));
        }

        void ConnectionMade(JToken token) {
            var data = (JObject) token;

            foreach (var prop in data) {
                new QObject(prop.Key, (JObject) prop.Value, channel);
            }

            // now unwrap properties, which might reference other registered objects
            foreach (var obj in channel.objects.Values) {
                obj.unwrapProperties();
            }
            if (initCallback != null) {
                initCallback(channel);
            }
            if (OnConnected != null) {
                OnConnected(this, new EventArgs());
            }
            connected.SetResult(true);

            channel.exec(new {type = (int) QWebChannelMessageTypes.Idle});
        }

        public void Send(object o)
        {
            Send(JsonConvert.SerializeObject(o));
        }

        public void Send(string s) {
            byte[] b = Encoding.UTF8.GetBytes(s);
            Send(b);
        }

        public void Send(byte[] b) {
            lock (channel.transport) {
                channel.transport.Send(b);
            }
        }

        void OnMessage(object sender, byte[] msg)
        {
            string jsonData = Encoding.UTF8.GetString(msg);
            JObject data = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(msg));

            lock (channel) {
                switch ((QWebChannelMessageTypes) (int) data["type"])
                {
                    case QWebChannelMessageTypes.Signal:
                        channel.handleSignal(data);
                        break;
                    case QWebChannelMessageTypes.Response:
                        channel.handleResponse(data);
                        break;
                    case QWebChannelMessageTypes.PropertyUpdate:
                        channel.handlePropertyUpdate(data);
                        break;
                    default:
                        Console.Error.WriteLine("invalid message received: " + jsonData);
                        break;
                }
            }
        }

        Dictionary<int, Delegate> execCallbacks = new Dictionary<int, Delegate>();
        uint execId = 0;

        public void exec(object data)
        {
            exec(data, null);
        }

        public void exec(object dataObject, Delegate callback)
        {
            var data = dataObject as JObject;

            if (data == null) {
                data = JObject.FromObject(dataObject);
            }

            if (callback == null) {
                // if no callback is given, send directly
                channel.Send(data);
                return;
            }

            if (data["id"] != null) {
                Console.Error.WriteLine("Cannot exec message with property id: " + data);
                return;
            }

            data["id"] = channel.execId++;
            channel.execCallbacks[(int) data["id"]] = callback;
            channel.Send(data);
        }

        public Dictionary<string, QObject> objects = new Dictionary<string, QObject>();

        void handleSignal(JToken message)
        {
            QObject obj;
            if (channel.objects.TryGetValue((string) message["object"], out obj)) {
                obj.signalEmitted((int) message["signal"], message["args"]);
            } else {
                Console.Error.WriteLine("Unhandled signal: " + message["object"] + "::" + message["signal"]);
            }
        }

        void handleResponse(JToken message)
        {
            if (message["id"] == null) {
                Console.Error.WriteLine("Invalid response message received: " + message);
                return;
            }

            channel.execCallbacks[(int) message["id"]].DynamicInvoke(message["data"]);
            channel.execCallbacks.Remove((int) message["id"]);
        }

        void handlePropertyUpdate(JToken message)
        {
            foreach (JToken data in message["data"]) {
                QObject obj = null;

                if (channel.objects.TryGetValue((string) data["object"], out obj)) {
                    obj.propertyUpdate((JObject) data["signals"], (JObject) data["properties"]);
                } else {
                    Console.Error.WriteLine("Unhandled property update: " + data["object"] + "::" + data["signal"]);
                }
            }

            channel.exec(new { type = (int) QWebChannelMessageTypes.Idle } );
        }

        void Debug(object message) {
            channel.Send(new { type = (int) QWebChannelMessageTypes.Debug, data = message });
        }

    }


    public class QObject : DynamicObject
    {
        public string __id__ = null;

        IDictionary<string, object> enums = new Dictionary<string, object>();

        IDictionary<string, int> methods = new Dictionary<string, int>();
        IDictionary<int, object> __propertyCache__ = new Dictionary<int, object>();
        IDictionary<string, int> properties = new Dictionary<string, int>();

        IDictionary<string, Signal> signals = new Dictionary<string, Signal>();

        public IDictionary<int, List<Delegate>> __objectSignals__ = new Dictionary<int, List<Delegate>>();

        public QWebChannel webChannel;

        public ICollection<string> Methods {
            get {
                return methods.Keys;
            }
        }

        public ICollection<string> Properties {
            get {
                return properties.Keys;
            }
        }

        public ICollection<string> Signals {
            get {
                return signals.Keys;
            }
        }

        public ICollection<string> Enums {
            get {
                return enums.Keys;
            }
        }

        public QObject(string name, JObject data, QWebChannel channel)
        {
            this.__id__ = name;
            this.webChannel = channel;

            webChannel.objects[name] = this;

            if (data["methods"] != null) {
                foreach (var method in data["methods"]) {
                    addMethod(method);
                }
            }

            if (data["properties"] != null) {
                foreach (var property in data["properties"]) {
                    bindGetterSetter(property);
                }
            }

            if (data["signals"] != null) {
                foreach (var signal in data["signals"]) {
                    addSignal(signal, false);
                }
            }

            if (data["enums"] != null) {
                foreach (var enumPair in (JObject) data["enums"]) {
                    enums[enumPair.Key] = (JObject) enumPair.Value;
                }
            }
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] arguments, out object invokeResult)
        {
            bool success = false;
            invokeResult = InvokeAsync(binder.Name, out success, arguments);
            return success;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            try {
                result = GetMember(binder.Name);
                return true;
            } catch (KeyNotFoundException) {
                result = null;
                return false;
            }
        }

        public override bool TrySetMember(SetMemberBinder binder, object value)
        {
            try {
                SetMember(binder.Name, value);
                return true;
            } catch (KeyNotFoundException) {
                return false;
            }
        }

        public bool Invoke(string name, params object[] arguments)
        {
            int methodIdx;

            bool haveMethod = methods.TryGetValue(name, out methodIdx);
            if (!haveMethod) {
                Console.Error.WriteLine("Unknown method {0}::{1}", __id__, name);
                return false;
            }

            List<object> args = new List<object>();
            Delegate callback = null;

            foreach (var argument in arguments) {
                var del = argument as Delegate;
                var qObj = argument as QObject;

                if (del != null) {
                    callback = del;
                } else if (qObj != null && webChannel.objects.ContainsKey(qObj.__id__)) {
                    args.Add(JObject.FromObject(new {
                        id = qObj.__id__
                    }));
                } else {
                    args.Add(argument);
                }
            }

            JObject msg = JObject.FromObject(new {
                type = (int) QWebChannelMessageTypes.InvokeMethod,
                method = methodIdx,
                args = args
            });

            msg["object"] = __id__;

            webChannel.exec(msg, (Action<JToken>) delegate(JToken response) {
                var result = unwrapQObject(response);
                if (callback != null) {
                    callback.DynamicInvoke(result);
                }
            });

            return true;
        }

        public Task<object> InvokeAsync(string name, out bool success, params object[] arguments)
        {
            var tcs = new TaskCompletionSource<object>();
            Action<object> callback = (result) => tcs.SetResult(result);

            // Enlarge the arguments array and put the callback into it
            var argsWithCallback = new object[arguments.Length + 1];
            arguments.CopyTo(argsWithCallback, 0);
            argsWithCallback[arguments.Length] = callback;

            success = Invoke(name, argsWithCallback);
            return tcs.Task;
        }

        public Task<object> InvokeAsync(string name, params object[] arguments)
        {
            bool success = false;
            return InvokeAsync(name, out success, arguments);
        }

        public int GetEnum(string enumName, string key)
        {
            JObject enum_ = (JObject) enums[enumName];

            return (int) enum_[key];
        }

        public object GetMember(string name)
        {
            int propId;
            bool haveProp = properties.TryGetValue(name, out propId);

            if (haveProp) {
                return __propertyCache__[propId];
            }

            Signal sig;

            bool haveSignal = signals.TryGetValue(name, out sig);
            if (haveSignal) {
                return sig;
            }

            object enum_;

            bool haveEnum = enums.TryGetValue(name, out enum_);
            if (haveEnum) {
                return enum_;
            }

            throw new KeyNotFoundException(string.Format("Member {0} not found in object {1}", name, __id__));
        }


        public void SetMember(string name, object value)
        {
            int propId;
            bool haveProp = properties.TryGetValue(name, out propId);

            if (!haveProp) {
                throw new KeyNotFoundException("No property named " + name + " in object " + __id__);
            }

            __propertyCache__[propId] = value;

            object valueToSend = value;

            var qObj = valueToSend as QObject;

            if (qObj != null && webChannel.objects.ContainsKey(qObj.__id__)) {
                valueToSend = new { id = qObj.__id__ };
            }

            var msg = JObject.FromObject(new {
                type = (int) QWebChannelMessageTypes.SetProperty,
                property = propId,
                value = valueToSend
            });

            msg["object"] = __id__;
            webChannel.exec(msg);
        }


        object unwrapQObject(object responseObj) {
            JToken response = responseObj as JToken;

            if (response == null) {
                return responseObj;
            }

            if (response is JArray) {
                // support list of objects
                var ret = (from o in (JArray) response select unwrapQObject(o)).ToArray();
                return ret;
            }

            if (response is JValue) {
                return ((JValue) response).Value;
            }

            if (!(response is JObject) || response == null) {
                return response;
            }

            if (response["__QObject*__"] == null || response["id"] == null) {
                Dictionary<string, object> ret = new Dictionary<string, object>();
                foreach (var prop in (JObject) response) {
                    ret[prop.Key] = unwrapQObject(prop.Value);
                }
                return ret;
            }

            string objectId = (string) response["id"];

            QObject existingObject;
            bool haveObject = webChannel.objects.TryGetValue(objectId, out existingObject);
            if (haveObject)
                return existingObject;

            if (response["data"] == null) {
                Console.Error.WriteLine("Cannot unwrap unknown QObject " + objectId + " without data.");
                return null;
            }

            QObject qObject = new QObject( objectId, (JObject) response["data"], webChannel );

            ((Signal) qObject.GetMember("destroyed")).connect((Action) delegate() {
                if (webChannel.objects[objectId] == qObject) {
                    webChannel.objects.Remove(objectId);
                }
            });

            // here we are already initialized, and thus must directly unwrap the properties
            qObject.unwrapProperties();
            return qObject;
        }

        public void signalEmitted(int signalName, JToken signalArgs)
        {
            object unwrapped = unwrapQObject(signalArgs);
            object[] unwrappedArray = unwrapped as object[];

            if (unwrappedArray != null) {
                invokeSignalCallbacks(signalName, unwrappedArray);
            } else {
                invokeSignalCallbacks(signalName, unwrapped);
            }
        }

        /**
        * Invokes all callbacks for the given signalname. Also works for property notify callbacks.
        */
        void invokeSignalCallbacks(int signalName, params object[] signalArgs)
        {
            List<Delegate> connections;

            bool found = __objectSignals__.TryGetValue(signalName, out connections);

            if (found) {
                foreach (var del in connections) {
                    invokeSignalCallback(del, signalArgs.Take(del.Method.GetParameters().Length).ToArray());
                }
            }
        }

        static Type EventHandlerGenericType = typeof(EventHandler<EventArgs>).GetGenericTypeDefinition();

        void invokeSignalCallback(Delegate del, object[] args)
        {
            var delType = del.GetType();
            if (delType.IsGenericType && delType.GetGenericTypeDefinition() == EventHandlerGenericType) {
                var eventArgsType = delType.GetGenericArguments()[0];
                var eventArgs = Activator.CreateInstance(eventArgsType, args);
                del.DynamicInvoke(this, eventArgs);
            } else if (delType == typeof(EventHandler)) {
                del.DynamicInvoke(this, new EventArgs());
            } else {
                del.DynamicInvoke(args);
            }
        }

        public void propertyUpdate(JObject signals, JObject propertyMap)
        {
            // update property cache
            foreach (var prop in propertyMap) {
                __propertyCache__[int.Parse(prop.Key)] = unwrapQObject(prop.Value);
            }

            foreach (var sig in signals) {
                // Invoke all callbacks, as signalEmitted() does not. This ensures the
                // property cache is updated before the callbacks are invoked.
                invokeSignalCallbacks(int.Parse(sig.Key), sig.Value.ToObject<object[]>());
            }
        }

        public void unwrapProperties()
        {
            foreach (var key in __propertyCache__.Keys.ToList()) {
                __propertyCache__[key] = unwrapQObject(__propertyCache__[key]);
            }
        }

        void addMethod(JToken method)
        {
            methods[(string) method[0]] = (int) method[1];
        }

        void bindGetterSetter(JToken propertyInfo)
        {
            int propertyIndex = (int) propertyInfo[0];
            string propertyName = (string) propertyInfo[1];
            JArray notifySignalData = (JArray) propertyInfo[2];

            // initialize property cache with current value
            // NOTE: if this is an object, it is not directly unwrapped as it might
            // reference other QObject that we do not know yet
            this.__propertyCache__[propertyIndex] = propertyInfo[3].ToObject<object>();

            if (notifySignalData.HasValues) {
                if (notifySignalData[0].Type == JTokenType.Integer && (int) notifySignalData[0] == 1) {
                    // signal name is optimized away, reconstruct the actual name
                    notifySignalData[0] = propertyName + "Changed";
                }
                addSignal(notifySignalData, true);
            }

            this.properties[propertyName] = propertyIndex;
        }

        void addSignal(JToken signalData, bool isPropertyNotifySignal)
        {
            string signalName = (string) signalData[0];
            int signalIndex = (int) signalData[1];

            signals[signalName] = new Signal(this, signalIndex, signalName, isPropertyNotifySignal);
        }
    }


    public class Signal
    {
        QObject qObject;
        int signalIndex;
        string signalName;
        bool isPropertyNotifySignal;

        public Signal(QObject qObject, int signalIndex, string signalName, bool isPropertyNotifySignal) {
            this.qObject = qObject;
            this.signalIndex = signalIndex;
            this.signalName = signalName;
            this.isPropertyNotifySignal = isPropertyNotifySignal;
        }

        public void connect(Delegate callback) {
            if (!qObject.__objectSignals__.ContainsKey(signalIndex)) {
                qObject.__objectSignals__[signalIndex] = new List<Delegate>();
            }

            qObject.__objectSignals__[signalIndex].Add(callback);

            if (!isPropertyNotifySignal && signalName != "destroyed") {
                // only required for "pure" signals, handled separately for properties in _propertyUpdate
                // also note that we always get notified about the destroyed signal
                var msg = new JObject();
                msg["type"] = (int) QWebChannelMessageTypes.ConnectToSignal;
                msg["object"] = qObject.__id__;
                msg["signal"] = signalIndex;

                qObject.webChannel.exec(msg);
            }
        }

        public void disconnect(Delegate callback) {
            if (!qObject.__objectSignals__.ContainsKey(signalIndex)) {
                qObject.__objectSignals__[signalIndex] = new List<Delegate>();
            }

            if (!qObject.__objectSignals__[signalIndex].Contains(callback)) {
                Console.Error.WriteLine("Cannot find connection of signal " + signalName + " to " + callback);
                return;
            }

            qObject.__objectSignals__[signalIndex].Remove(callback);

            if (!isPropertyNotifySignal && qObject.__objectSignals__[signalIndex].Count == 0) {
                // only required for "pure" signals, handled separately for properties in _propertyUpdate
                var msg = new JObject();
                msg["type"] = (int) QWebChannelMessageTypes.DisconnectFromSignal;
                msg["object"] = qObject.__id__;
                msg["signal"] = signalIndex;

                qObject.webChannel.exec(msg);
            }
        }
    }
}
