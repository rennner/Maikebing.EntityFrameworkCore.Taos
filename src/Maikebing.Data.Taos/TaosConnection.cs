﻿// Copyright (c)  maikebing All rights reserved.
//// Licensed under the MIT License, See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using TDengineDriver;

namespace Maikebing.Data.Taos
{
    /// <summary>
    ///     Represents a connection to a Taos database.
    /// </summary>
    public partial class TaosConnection : DbConnection
    {
        private string configDir = "C:/TDengine/cfg";

        private readonly IList<WeakReference<TaosCommand>> _commands = new List<WeakReference<TaosCommand>>();

        private string _connectionString;
        private ConnectionState _state;
        internal IntPtr _taos;

        private static bool  _dll_isloaded=false;
        public   byte[] Binary( Assembly _assembly, string name)
        {
            using (MemoryStream memoryStream = new MemoryStream())
            {
                (_assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Resource {name} not available.")).CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }
        /// <summary>
        ///     Initializes a new instance of the <see cref="TaosConnection" /> class.
        /// </summary>
        public TaosConnection()
        {  
            if (_dll_isloaded == false)
            {
                var libManager = new LibraryManager(
                    new LibraryItem(Platform.Windows, Bitness.x64,
                        new LibraryFile("taos.dll", Binary(typeof(TaosConnection).Assembly, $"{typeof(TaosConnection).Namespace}.libs.taos_x64.dll"))),
                     new LibraryItem(Platform.Windows, Bitness.x32,
                        new LibraryFile("taos.dll", Binary(typeof(TaosConnection).Assembly, $"{typeof(TaosConnection).Namespace}.libs.taos_x32.dll"))),
                    new LibraryItem(Platform.Linux, Bitness.x64,
                        new LibraryFile("libtaos.so", Binary(typeof(TaosConnection).Assembly, $"{typeof(TaosConnection).Namespace}.libs.libtaos_x64.so"))));
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    configDir = "/etc/taos";
                 
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    configDir = "C:/TDengine/cfg";
                }
                if (!System.IO.Directory.Exists(configDir))
                {
                    configDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "taos.studio");
                }
                var cfg = new System.IO.FileInfo(System.IO.Path.Combine(configDir, "taos.cfg"));
                if (!cfg.Directory.Exists) cfg.Directory.Create();
                if (!cfg.Exists)
                {
                     System.IO.File.WriteAllBytes(cfg.FullName,  Binary(typeof(TaosConnection).Assembly, $"{typeof(TaosConnection).Namespace}.cfg.taos.cfg"));
                }
                if ((RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.OSArchitecture == Architecture.X64)
                    || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X64)
                    || (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && RuntimeInformation.OSArchitecture == Architecture.X86))
                {
                    TDengine.Options((int)TDengineInitOption.TDDB_OPTION_CONFIGDIR, this.configDir);
                    TDengine.Options((int)TDengineInitOption.TDDB_OPTION_SHELL_ACTIVITY_TIMER, "60");
                    TDengine.Init();
                    Process.GetCurrentProcess().Disposed += (object sender, EventArgs e) =>
                        {
                            TDengine.Cleanup();
                        };
                    _dll_isloaded = true;
                }
                else
                {
                    throw new PlatformNotSupportedException("Only Support Linux X64 And Windows X64/X86");
                }
            }
        }

       
        /// <summary>
        ///     Initializes a new instance of the <see cref="TaosConnection" /> class.
        /// </summary>
        /// <param name="connectionString">The string used to open the connection.</param>
        /// <seealso cref="TaosConnectionStringBuilder" />
        public TaosConnection(string connectionString) : this()
        {
            ConnectionStringBuilder = new TaosConnectionStringBuilder(connectionString);
            ConnectionString = connectionString;
        }



        /// <summary>
        ///     Gets or sets a string used to open the connection.
        /// </summary>
        /// <value>A string used to open the connection.</value>
        /// <seealso cref="TaosConnectionStringBuilder" />
        public override string ConnectionString
        {
            get => _connectionString;
            set
            {
                _connectionString = value;
                ConnectionStringBuilder = new TaosConnectionStringBuilder(value);
                TDengine.Options((int)TDengineInitOption.TSDB_OPTION_CHARSET, ConnectionStringBuilder.Charset);
            }
        }

        internal TaosConnectionStringBuilder ConnectionStringBuilder { get; set; }


        /// <summary>
        ///     Gets the path to the database file. Will be absolute for open connections.
        /// </summary>
        /// <value>The path to the database file.</value>
        public override string DataSource
        {
            get
            {
                string dataSource = null;

                return dataSource ?? ConnectionStringBuilder.DataSource;
            }
        }

        /// <summary>
        ///     Gets or sets the default <see cref="TaosCommand.CommandTimeout"/> value for commands created using
        ///     this connection. This is also used for internal commands in methods like
        ///     <see cref="BeginTransaction()"/>.
        /// </summary>
        /// <value>The default <see cref="TaosCommand.CommandTimeout"/> value</value>
        public virtual int DefaultTimeout { get; set; } = 60;


        string _version = string.Empty;
        /// <summary>
        ///     Gets the version of Taos used by the connection.
        /// </summary>
        /// <value>The version of Taos used by the connection.</value>
        public override string ServerVersion
        {
            get
            {
                if (_taos == IntPtr.Zero)
                {
                    TaosException.ThrowExceptionForRC(-10005, "Connection is not open",null);
                }
               else  if (string.IsNullOrEmpty(_version))
                {
                    _version = Marshal.PtrToStringAnsi(TDengine.GetServerInfo(_taos));
                }
                return _version;
            }
        }
        public   string ClientVersion
        {
            get
            {
                if (string.IsNullOrEmpty(_version))
                {
                    _version = Marshal.PtrToStringAnsi(TDengine.GetClientInfo());
                }
                return _version;
            }
        }
        /// <summary>
        ///     Gets the current state of the connection.
        /// </summary>
        /// <value>The current state of the connection.</value>
        public override ConnectionState State
            => _state;

        /// <summary>
        ///     Gets the <see cref="DbProviderFactory" /> for this connection.
        /// </summary>
        /// <value>The <see cref="DbProviderFactory" />.</value>
        protected override DbProviderFactory DbProviderFactory
            => TaosFactory.Instance;

        /// <summary>
        ///     Gets or sets the transaction currently being used by the connection, or null if none.
        /// </summary>
        /// <value>The transaction currently being used by the connection.</value>
        protected internal virtual TaosTransaction Transaction { get; set; }

        public override string Database => ConnectionStringBuilder.DataBase;




        private void SetState(ConnectionState value)
        {
            var originalState = _state;
            if (originalState != value)
            {
                _state = value;
                OnStateChange(new StateChangeEventArgs(originalState, value));
            }
        }

        /// <summary>
        ///     Opens a connection to the database using the value of <see cref="ConnectionString" />. If
        ///     <c>Mode=ReadWriteCreate</c> is used (the default) the file is created, if it doesn't already exist.
        /// </summary>
        /// <exception cref="TaosException">A Taos error occurs while opening the connection.</exception>
        public override void Open()
        {
       
            if (State == ConnectionState.Open)
            {
                return;
            }
            if (ConnectionString == null)
            {
                throw new InvalidOperationException("Open Requires Set ConnectionString");
            }

            this._taos = TDengine.Connect(this.DataSource, ConnectionStringBuilder.Username, ConnectionStringBuilder.Password,"", ConnectionStringBuilder.Port);
           if (this._taos == IntPtr.Zero)
            {
                TaosException.ThrowExceptionForRC(_taos);
            }
            else
            {
                SetState(ConnectionState.Open);
                if (!string.IsNullOrEmpty( ConnectionStringBuilder.DataBase))
                {
                    this.ChangeDatabase(ConnectionStringBuilder.DataBase);
                }
            }
        }

        /// <summary>
        ///     Closes the connection to the database. Open transactions are rolled back.
        /// </summary>
        public override void Close()
        {
            if (State != ConnectionState.Closed)
                TDengine.Close(_taos);

            Transaction?.Dispose();

            foreach (var reference in _commands)
            {
                if (reference.TryGetTarget(out var command))
                {
                    command.Dispose();
                }
            }

            _commands.Clear();


            SetState(ConnectionState.Closed);
        }

        /// <summary>
        ///     Releases any resources used by the connection and closes it.
        /// </summary>
        /// <param name="disposing">
        ///     true to release managed and unmanaged resources; false to release only unmanaged resources.
        /// </param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Close();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        ///     Creates a new command associated with the connection.
        /// </summary>
        /// <returns>The new command.</returns>
        /// <remarks>
        ///     The command's <seealso cref="TaosCommand.Transaction" /> property will also be set to the current
        ///     transaction.
        /// </remarks>
        public new virtual TaosCommand CreateCommand()
            => new TaosCommand { Connection = this, CommandTimeout = DefaultTimeout, Transaction = Transaction };
        public virtual TaosCommand CreateCommand(string commandtext)
          => new TaosCommand { Connection = this, CommandText = commandtext, CommandTimeout = DefaultTimeout, Transaction = Transaction };

        /// <summary>
        ///     Creates a new command associated with the connection.
        /// </summary>
        /// <returns>The new command.</returns>
        protected override DbCommand CreateDbCommand()
            => CreateCommand();

        internal void AddCommand(TaosCommand command)
            => _commands.Add(new WeakReference<TaosCommand>(command));

        internal void RemoveCommand(TaosCommand command)
        {
            for (var i = _commands.Count - 1; i >= 0; i--)
            {
                if (!_commands[i].TryGetTarget(out var item)
                    || item == command)
                {
                    _commands.RemoveAt(i);
                }
            }
        }

        /// <summary>
        ///     Create custom collation.
        /// </summary>
        /// <param name="name">Name of the collation.</param>
        /// <param name="comparison">Method that compares two strings.</param>
        public virtual void CreateCollation(string name, Comparison<string> comparison)
            => CreateCollation(name, null, comparison != null ? (_, s1, s2) => comparison(s1, s2) : (Func<object, string, string, int>)null);

        /// <summary>
        ///     Create custom collation.
        /// </summary>
        /// <typeparam name="T">The type of the state object.</typeparam>
        /// <param name="name">Name of the collation.</param>
        /// <param name="state">State object passed to each invocation of the collation.</param>
        /// <param name="comparison">Method that compares two strings, using additional state.</param>
        public virtual void CreateCollation<T>(string name, T state, Func<T, string, string, int> comparison)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (State != ConnectionState.Open)
            {
                throw new InvalidOperationException($"CallRequiresOpenConnection{nameof(CreateCollation)}");
            }


        }

        /// <summary>
        ///     Begins a transaction on the connection.
        /// </summary>
        /// <returns>The transaction.</returns>
        public new virtual TaosTransaction BeginTransaction()
            => BeginTransaction(IsolationLevel.Unspecified);

        /// <summary>
        ///     Begins a transaction on the connection.
        /// </summary>
        /// <param name="isolationLevel">The isolation level of the transaction.</param>
        /// <returns>The transaction.</returns>
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => BeginTransaction(isolationLevel);

        /// <summary>
        ///     Begins a transaction on the connection.
        /// </summary>
        /// <param name="isolationLevel">The isolation level of the transaction.</param>
        /// <returns>The transaction.</returns>
        public new virtual TaosTransaction BeginTransaction(IsolationLevel isolationLevel)
        {
            if (State != ConnectionState.Open)
            {
                throw new InvalidOperationException($"CallRequiresOpenConnection{nameof(BeginTransaction)}");
            }
            if (Transaction != null)
            {
                throw new InvalidOperationException($"ParallelTransactionsNotSupported");
            }

            return Transaction = new TaosTransaction(this, isolationLevel);
        }

        /// <summary>
        ///     Changes the current database.  
        /// </summary>
        /// <param name="databaseName">The name of the database to use.</param>
        public override void ChangeDatabase(string databaseName)
        {
             int result = TDengine.SelectDatabase(_taos, databaseName);
           // int result = this.CreateCommand($" use {databaseName};").ExecuteNonQuery();
            Debug.WriteLine($"Select Database {databaseName} ,result is {result}");

        }

        private class AggregateContext<T>
        {
            public AggregateContext(T seed)
                => Accumulate = seed;

            public T Accumulate { get; set; }
            public Exception Exception { get; set; }
        }
    }
}
