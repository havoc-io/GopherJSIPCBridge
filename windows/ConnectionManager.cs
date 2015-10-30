using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace GopherJSIPCBridge
{
    public class ConnectionManager
    {
        // The next connection id
        private Int32 _nextConnectionId;

        // Map from connection id to pipe stream.  Values may be either a
        // NamedPipeClientStream or NamedPipeServerStream.
        private Dictionary<Int32, PipeStream> _connections;

        // The next listener id
        private Int32 _nextListenerId;

        // Map from listener id to named pipe name.  A new NamedPipeServerStream
        // has to be created for each accept call, so we simply store the
        // endpoint here.
        private Dictionary<Int32, string> _listeners;

        // Constructor
        public ConnectionManager()
        {
            // Set up identifiers
            _nextConnectionId = 0;
            _nextListenerId = 0;

            // Create our maps
            _connections = new Dictionary<Int32, PipeStream>();
            _listeners = new Dictionary<Int32, string>();
        }

        // Asynchronously create a new connection
        public async Task<Tuple<Int32, string>> ConnectAsync(string endpoint)
        {
            // Parse the endpoint.  It should be formatted as
            // "\\server\pipe\name".
            string [] components = endpoint.Split(new char[] { '\\' });
            if (components.Length != 5)
            {
                return Tuple.Create(-1, "invalid endpoint format");
            }

            // Create the connection
            // NOTE: It is essential that the PipeOptions.Asynchronous option be
            // specified, or the ReadAsync and WriteAsync methods will block
            // (and I don't mean they'll call await and halt - I mean they'll
            // never return a Task object)
            var connection = new NamedPipeClientStream(
                components[2],
                components[4],
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            );

            // Store the connection
            Int32 connectionId = -1;
            lock (this)
            {
                // Compute the next connection id.  Just to be paranoid, make
                // sure we don't overflow the maximum value, because we use -1
                // as the invalid identifier.
                if (_nextConnectionId < 0)
                {
                    return Tuple.Create(-1, "connection ids exhausted");
                }
                connectionId = _nextConnectionId++;

                // Do the storage
                _connections[connectionId] = connection;
            }

            // Connect asynchronously
            try
            {
                await connection.ConnectAsync();
            }
            catch(Exception e)
            {
                // If there was an error, remove the connection
                lock (this)
                {
                    _connections.Remove(connectionId);
                }

                // Bail
                return Tuple.Create(-1, e.Message);
            }

            // All done
            return Tuple.Create(connectionId, "");
        }

        // Asynchronously read from a connection
        public async Task<Tuple<Int32, string>> ConnectionReadAsync(
            Int32 connectionId,
            byte[] buffer
        )
        {
            // Get the connection
            PipeStream connection = null;
            lock (this)
            {
                if (!_connections.TryGetValue(connectionId, out connection))
                {
                    return Tuple.Create(0, "invalid connection id");
                }
            }

            // Handle the case of 0 read length.  It's technically not an error,
            // but there is no need to do it asynchronously.
            if (buffer.Length == 0)
            {
                return Tuple.Create(0, "");
            }

            // Read asynchronously
            // HACK: The ReadAsync method technically doesn't say that it will
            // return a non-empty buffer if there is no error, so it could
            // conceivably return an empty buffer without an error.  This is
            // discouraged in Go's io.Reader interface, so if we encounter this
            // situation, just re-queue the read.
            Int32 count = 0;
            while (count == 0)
            {
                // Try reading asynchronously
                try
                {
                    count = await connection.ReadAsync(
                        buffer,
                        0,
                        buffer.Length
                    );
                }
                catch (Exception e)
                {
                    // NOTE: There may be an error but a non-0 quantity of data,
                    // unlike in the write case where it's all-or-nothing
                    return Tuple.Create(count, e.Message);
                }
            }

            // All done
            return Tuple.Create(count, "");
        }

        // Asynchronously write to a connection
        public async Task<Tuple<Int32, string>> ConnectionWriteAsync(
            Int32 connectionId,
            byte[] buffer
        )
        {
            // Get the connection
            PipeStream connection = null;
            lock (this)
            {
                if (!_connections.TryGetValue(connectionId, out connection))
                {
                    return Tuple.Create(0, "invalid connection id");
                }
            }

            // Handle the case of 0 write length.  It's technically not an
            // error, but there is no need to do it asynchronously.
            if (buffer.Length == 0)
            {
                return Tuple.Create(0, "");
            }

            // Try writing asynchronously.  This is will wait until all data has
            // been written or there is an error, which matches that Go
            // io.Writer semantics nicely.
            // NOTE: It's not clear from the documentation if the
            // Write/WriteAsync methods can do partial writes, i.e. write some
            // of the data and then fail.  They don't provide a mechanism for
            // getting the number of bytes written, so I assume they either
            // succeed and write everything or fail and write nothing.  In any
            // case, most clients will close a connection when writes fail, so
            // maybe it doesn't matter, but it'd be worth finding out.
            try
            {
                await connection.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception e)
            {
                // If there was an error, bail
                // NOTE: See note above, but we assume failure means nothing was
                // written
                return Tuple.Create(0, e.Message);
            }

            // All done
            // NOTE: See note above, but we assume success means all data was
            // written
            return Tuple.Create(buffer.Length, "");
        }

        // Asynchronously close a connection
        public async Task<string> ConnectionCloseAsync(Int32 connectionId)
        {
            // Get the connection
            PipeStream connection = null;
            lock (this)
            {
                if (!_connections.TryGetValue(connectionId, out connection))
                {
                    return "invalid connection id";
                }
            }

            // Close the connection.  There is no asynchronous method for doing
            // this, so we have to emulate it.
            string result = await Task.Run(() =>
            {
                // Try to close the connection
                try
                {
                    connection.Close();
                }
                catch (Exception e)
                {
                    return e.Message;
                }

                // Success
                return "";
            });

            // Remove it from the map if we were successful
            if (result == "")
            {
                lock (this)
                {
                    _connections.Remove(connectionId);
                }
            }

            // All done
            return result;
        }

        // TODO: Implement listener methods
    }
}
