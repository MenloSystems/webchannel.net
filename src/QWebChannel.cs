/*
 * This file is part of WebChannel.NET.
 * Copyright (C) 2016 - 2018, Menlo Systems GmbH
 * Copyright (C) 2016 The Qt Company Ltd.
 * Copyright (C) 2016 Klarälvdalens Datakonsult AB, a KDAB Group company
 * License: Dual-licensed under LGPLv3 and GPLv2+
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.Linq;
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

    public delegate void SignalEventHandler(object sender, object[] args);

    public class QWebChannel
    {
        IWebChannelTransport transport;
        TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
        internal IDictionary<string, QObject> objects = new Dictionary<string, QObject>();
        public ReadOnlyDictionary<string, QObject> Objects { get; }
        internal object lockObject = new object();

        public Task IsConnected { get { return connected.Task; } }
        public event EventHandler OnConnected;

        public QWebChannel(IWebChannelTransport transport, Action<QWebChannel> initCallback = null)
        {
            Objects = new ReadOnlyDictionary<string, QObject>(objects);

            this.transport = transport;
            this.transport.OnMessage += this.OnMessage;

            if (initCallback != null) {
                OnConnected += (sender, args) => initCallback(this);
            }

            exec(new { type = (int) QWebChannelMessageTypes.Init }, new Action<JToken>(ConnectionMade));
        }

        void ConnectionMade(JToken token) {
            var data = (JObject) token;

            foreach (var prop in data) {
                new QObject(prop.Key, (JObject) prop.Value, this);
            }

            // now unwrap properties, which might reference other registered objects
            foreach (var obj in objects.Values.ToArray()) {
                obj.unwrapProperties();
            }
            if (OnConnected != null) {
                OnConnected(this, new EventArgs());
            }
            connected.SetResult(true);

            exec(new {type = (int) QWebChannelMessageTypes.Idle});
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
            transport.Send(b);
        }

        void OnMessage(object sender, byte[] msg)
        {
            string jsonData = Encoding.UTF8.GetString(msg);
            JObject data = JsonConvert.DeserializeObject<JObject>(Encoding.UTF8.GetString(msg));

            lock (lockObject) {
                switch ((QWebChannelMessageTypes) (int) data["type"])
                {
                    case QWebChannelMessageTypes.Signal:
                        handleSignal(data);
                        break;
                    case QWebChannelMessageTypes.Response:
                        handleResponse(data);
                        break;
                    case QWebChannelMessageTypes.PropertyUpdate:
                        handlePropertyUpdate(data);
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
                Send(data);
                return;
            }

            if (data["id"] != null) {
                Console.Error.WriteLine("Cannot exec message with property id: " + data);
                return;
            }

            lock (lockObject) {
                data["id"] = execId++;
                execCallbacks[(int) data["id"]] = callback;
            }
            Send(data);
        }

        void handleSignal(JToken message)
        {
            QObject obj;
            if (objects.TryGetValue((string) message["object"], out obj)) {
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

            execCallbacks[(int) message["id"]].DynamicInvoke(message["data"]);
            execCallbacks.Remove((int) message["id"]);
        }

        void handlePropertyUpdate(JToken message)
        {
            foreach (JToken data in message["data"]) {
                QObject obj = null;

                if (objects.TryGetValue((string) data["object"], out obj)) {
                    obj.propertyUpdate((JObject) data["signals"], (JObject) data["properties"]);
                } else {
                    Console.Error.WriteLine("Unhandled property update: " + data["object"] + "::" + data["signal"]);
                }
            }

            exec(new { type = (int) QWebChannelMessageTypes.Idle } );
        }

        void Debug(object message) {
            Send(new { type = (int) QWebChannelMessageTypes.Debug, data = message });
        }

    }


    public class QObject : DynamicObject
    {
        public string __id__ = null;

        IDictionary<string, EnumWrapper> enums = new Dictionary<string, EnumWrapper>();

        IDictionary<string, int> methods = new Dictionary<string, int>();
        IDictionary<int, object> __propertyCache__ = new Dictionary<int, object>();
        IDictionary<string, int> properties = new Dictionary<string, int>();

        IDictionary<string, Signal> signals = new Dictionary<string, Signal>();
        internal object lockObject = new object();

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

        public ReadOnlyDictionary<string, EnumWrapper> Enums { get; }

        public override string ToString() {
            string name = (string) GetMember("objectName");
            if (name.Length > 0) {
                return string.Format("QObject({0}, name = \"{1}\")", __id__, name);
            } else {
                return string.Format("QObject({0})", __id__);
            }
        }

        public QObject(string name, JObject data, QWebChannel channel)
        {
            this.__id__ = name;
            this.webChannel = channel;

            lock (webChannel.lockObject) {
                webChannel.objects[name] = this;
            }

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
                    enums[enumPair.Key] = new EnumWrapper(enumPair.Key, (JObject) enumPair.Value);
                }
            }

            Enums = new ReadOnlyDictionary<string, EnumWrapper>(enums);
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
            object invokedMethod = null;

            // Fully specified methods are invoked by id, others by name for host-side overload resolution
            if (!name.EndsWith(")")) {
                invokedMethod = name;
            } else {
                int methodIdx;
                bool haveMethod = methods.TryGetValue(name, out methodIdx);
                if (!haveMethod) {
                    Console.Error.WriteLine("Unknown method {0}::{1}", __id__, name);
                    return false;
                }
                invokedMethod = methodIdx;
            }

            List<object> args = new List<object>();
            Delegate callback = null;

            // Lock on the QWebChannel object because we're inspecting its objects
            lock (webChannel.lockObject) {
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
            }

            JObject msg = JObject.FromObject(new {
                type = (int) QWebChannelMessageTypes.InvokeMethod,
                method = invokedMethod,
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

        public int GetEnumValue(string enumName, string key)
        {
            lock (lockObject) {
                return Enums[enumName][key];
            }
        }

        public object GetMember(string name)
        {
            lock (lockObject) {
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

                EnumWrapper enum_;

                bool haveEnum = enums.TryGetValue(name, out enum_);
                if (haveEnum) {
                    return enum_;
                }
            }

            throw new KeyNotFoundException(string.Format("Member {0} not found in object {1}", name, __id__));
        }


        public void SetMember(string name, object value)
        {
            int propId;
            lock (lockObject) {
                bool haveProp = properties.TryGetValue(name, out propId);

                if (!haveProp) {
                    throw new KeyNotFoundException("No property named " + name + " in object " + __id__);
                }

                __propertyCache__[propId] = value;
            }

            object valueToSend = value;

            var qObj = valueToSend as QObject;

            lock (webChannel.lockObject) {
                if (qObj != null && webChannel.objects.ContainsKey(qObj.__id__)) {
                    valueToSend = new { id = qObj.__id__ };
                }
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

            lock (webChannel.lockObject) {
                QObject existingObject;
                bool haveObject = webChannel.objects.TryGetValue(objectId, out existingObject);
                if (haveObject)
                    return existingObject;
            }

            if (response["data"] == null) {
                Console.Error.WriteLine("Cannot unwrap unknown QObject " + objectId + " without data.");
                return null;
            }

            QObject qObject = new QObject( objectId, (JObject) response["data"], webChannel );

            ((Signal) qObject.GetMember("destroyed")).Connect(() => {
                lock (webChannel.lockObject) {
                    if (webChannel.objects[objectId] == qObject) {
                        webChannel.objects.Remove(objectId);
                    }
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

            bool found = false;
            lock (lockObject) {
                found = __objectSignals__.TryGetValue(signalName, out connections);
            }

            if (found) {
                foreach (var del in connections) {
                    invokeSignalCallback(del, signalArgs);
                }
            }
        }

        void invokeSignalCallback(Delegate del, object[] args)
        {
            var sigHandler = del as Action<object[]>;
            if (sigHandler != null) {
                sigHandler(args);
            } else {
                // Support connections to delegates with less parameters than transmitted
                var slicedArgs = args.Take(del.Method.GetParameters().Length).ToArray();
                del.DynamicInvoke(slicedArgs);
            }
        }

        public void propertyUpdate(JObject signals, JObject propertyMap)
        {
            lock (lockObject) {
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
        }

        public void unwrapProperties()
        {
            lock (lockObject) {
                foreach (var key in __propertyCache__.Keys.ToList()) {
                    __propertyCache__[key] = unwrapQObject(__propertyCache__[key]);
                }
            }
        }

        // Don't do any locking in this method - it's only used from the constructor
        void addMethod(JToken method)
        {
            methods[(string) method[0]] = (int) method[1];
        }

        // Don't do any locking in this method - it's only used from the constructor
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

        // Don't do any locking in this method - it's only used from the constructor
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

        public event SignalEventHandler OnEmission {
            add {
                Connect(delegate (object[] args) {
                    value(qObject, args);
                });
            }
            remove {
                throw new NotImplementedException("Not yet implemented!");
            }
        }

        public Signal(QObject qObject, int signalIndex, string signalName, bool isPropertyNotifySignal) {
            this.qObject = qObject;
            this.signalIndex = signalIndex;
            this.signalName = signalName;
            this.isPropertyNotifySignal = isPropertyNotifySignal;
        }

        public void Connect(Delegate callback) {
            lock (qObject.lockObject) {
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
        }

        public void Connect(Action callback) => Connect((Delegate) callback);
        public void Connect<T>(Action<T> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2>(Action<T1, T2> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3>(Action<T1, T2, T3> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3, T4>(Action<T1, T2, T3, T4> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3, T4, T5>(Action<T1, T2, T3, T4, T5> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3, T4, T5, T6>(Action<T1, T2, T3, T4, T5, T6> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3, T4, T5, T6, T7>(Action<T1, T2, T3, T4, T5, T6, T7> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3, T4, T5, T6, T7, T8>(Action<T1, T2, T3, T4, T5, T6, T7, T8> callback) => Connect((Delegate) callback);
        public void Connect<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> callback) => Connect((Delegate) callback);

        public void Disconnect(Delegate callback) {
            lock (qObject.lockObject) {
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

    public class EnumWrapper
    {
        JObject def;
        public JObject UnderylingObject { get => def; }
        public string Name { get; }

        public EnumWrapper(string name, JObject def) {
            this.def = def;
            this.Name = name;
        }

        public string KeyFromValue(object value) {
            var tok = JToken.FromObject(value);
            return KeyFromValue(tok);
        }

        public string KeyFromValue(JToken value) {
            var p = def.Properties().FirstOrDefault(pair => pair.Value.Equals(value));
            return p != null ? p.Name : "";
        }

        public int Value(string key) {
            JToken tok;
            def.TryGetValue(key, out tok);
            if (tok == null) {
                throw new KeyNotFoundException(string.Format("Enum {0} has no member {1}", Name, key));
            }
            return tok.Value<int>();
        }

        public int this[string key] {
            get => Value(key);
        }

        public override string ToString() {
            return Name + ' ' + def.ToString();
        }
    }
}
