///////////////////////////////////////////////////////////////////////////////
//
// KitKatTimer
// Copyright (C) 2012 Ashwin Nanjappa
// Released under the MIT License
//
// "Have a break ... have a Kit Kat!"
//
// - A simple application that runs in the system tray and notifies you when you
//   have been working too long.
// - Notifies using Snarl and communicates with it using SNP (Snarl Network
//   Protocol).
// - Can sense when the system is idle, put to sleep/hibernate or when locked.
// - Icon from: http://www.fatcow.com/free-icons
//
///////////////////////////////////////////////////////////////////////////////

using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.ApplicationServices;

namespace KitKatTimer
{
    class KitKatTimerApp : Form
    {
        NotifyIcon  _trayIcon;
        IPAddress   _address;
        Int32       _port;
        IPEndPoint  _endPoint;
        Socket      _socket;
        DateTime    _lastTime;
        TimeSpan    _breakSpan;
        Timer       _timer;
        TimeSpan[]  _spanArr;
        Boolean     _isAway;
        Boolean     _isRegistered;
        Int32       _rand;

        public KitKatTimerApp()
        {
            ////
            // Initialize times
            ////

            _spanArr = new TimeSpan[ 5 ];

            for ( int i = 0; i < _spanArr.Length; ++i )
            {
                _spanArr[ i ] = ( 0 == i ) ? TimeSpan.FromHours( 0.5 ) : TimeSpan.FromHours( i );
                //_spanArr[ i ] = ( 0 == i ) ? TimeSpan.FromSeconds( 10 ) : TimeSpan.FromSeconds( ( i + 1 ) * 10 );
            }

            Int32 defSpanIndex  = 1;
            _breakSpan          = _spanArr[ defSpanIndex ];
            _lastTime           = DateTime.Now;
            _isAway             = false;
            _isRegistered       = false;
            _rand               = 0;
            _address            = IPAddress.Parse( "127.0.0.1" );
            _port               = 9887;

            ////
            // Set handlers for system status changes
            ////

            SystemEvents.SessionSwitch      += new SessionSwitchEventHandler( lockedHandler );
            SystemEvents.PowerModeChanged   += new PowerModeChangedEventHandler( hibernateHandler );
            PowerManager.IsMonitorOnChanged += new EventHandler( idleHandler );

            ////
            // Create system tray icon and its menu
            ////

            _trayIcon       = new NotifyIcon();
            _trayIcon.Icon  = KitKatTimerResources.time_go; // Set system tray icon 
            _trayIcon.Text  = "KitKatTimer";

            MenuItem[] subMenu = new MenuItem[ _spanArr.Length ];

            for ( int i = 0; i < _spanArr.Length; ++i )
            {
                String timeStr          = spanToString( _spanArr[ i ] );
                subMenu[ i ]            = new MenuItem( timeStr, spanClickHandler );
                subMenu[ i ].Checked    = ( defSpanIndex == i ) ? true : false;
            }

            MenuItem[] mainMenu = new MenuItem[ 3 ];
            mainMenu[ 0 ]       = new MenuItem( "Remind me after", subMenu );
            mainMenu[ 1 ]       = new MenuItem( "About", aboutHandler );
            mainMenu[ 2 ]       = new MenuItem( "Exit", exitMenuHandler );

            _trayIcon.ContextMenu   = new ContextMenu( mainMenu );
            _trayIcon.Visible       = true;

            ////
            // Start timer
            ////

            _timer          = new Timer();
            _timer.Interval = 1000;
            _timer.Tick     += new EventHandler( timerHandler );
            _timer.Start();

            return;
        }

        // Hide form by overriding its visibility setting
        // This hides both the form window and its taskbar icon
        protected override void SetVisibleCore(bool value)
        {            
            base.SetVisibleCore( false );
            return;
        }

        void spanClickHandler( object sender, EventArgs e )
        {
            MenuItem clickedMenuItem        = ( MenuItem ) sender;
            Int32 clickedIndex              = clickedMenuItem.Index;
            Menu.MenuItemCollection subMenu = clickedMenuItem.Parent.MenuItems;

            for ( int i = 0; i < _spanArr.Length; ++i )
            {
                subMenu[ i ].Checked = ( clickedIndex == i ) ? true : false;
            }

            _breakSpan  = _spanArr[ clickedIndex ];
            _lastTime   = DateTime.Now; // Restart time

            return;
        }

        void lockedHandler( object sender, SessionSwitchEventArgs e )
        {
            // User locked or remotely disconnected
            if (    ( SessionSwitchReason.SessionLock == e.Reason )
                ||  ( SessionSwitchReason.RemoteDisconnect == e.Reason ) )
            {
                _isAway = true;
            }
            // User unlocked or remotely connected
            else if (   ( SessionSwitchReason.SessionUnlock == e.Reason )
                    ||  ( SessionSwitchReason.RemoteConnect == e.Reason ) )
            {
                _isAway     = false;
                _lastTime   = DateTime.Now; // Restart time
            }
            else
            {
                // Ignore other reasons
            }

            return;
        }

        void hibernateHandler( object sender, PowerModeChangedEventArgs e )
        {
            if ( PowerModes.Suspend == e.Mode )     // On hibernate/sleep
            {
                _isAway = true;
            }
            else if ( PowerModes.Resume == e.Mode ) // On resume from hibernate/sleep
            {
                _isAway     = false;
                _lastTime   = DateTime.Now; // Restart time
            }
            else
            {
                // Change from/to battery to/from power
                // This is not useful for user activity
            }

            return;
        }

        // Called when display is put to sleep or resumed
        // Monitor is put to sleep when system is idle, so user must be away
        void idleHandler( object sender, EventArgs e )
        {
            _isAway = !PowerManager.IsMonitorOn;

            if ( PowerManager.IsMonitorOn )
            {
                _lastTime = DateTime.Now; // Restart time
            }

            return;
        }

        // Called every second
        void timerHandler( object sender, EventArgs e )
        {
            // Do nothing if user is away
            if ( _isAway )
            {
                return;
            }

            // Maintain connection with Snarl
            registerWithSnarl();

            // Compute user's work period
            TimeSpan timeDiff = DateTime.Now.Subtract( _lastTime );

            // Update system tray icon status
            _trayIcon.Text  = _isRegistered
                            ? ( "KitKatTimer: It's been " + spanToString( timeDiff ) )
                            : ( "KitKatTimer: Trying to connect to Snarl" );

            // Check if user needs a break
            if ( timeDiff.CompareTo( _breakSpan ) > 0 )
            {
                notifySnarl(); // Tell user to take a break
            }

            return;
        }

        void registerWithSnarl()
        {
            // Already registered
            if ( _isRegistered )
            {
                return;
            }

            // Connect and register with Snarl

            try
            {
                _endPoint   = new IPEndPoint( _address, _port );
                _socket     = new Socket( AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp );
                
                _socket.Connect( _endPoint );

                byte[] registerMsg = Encoding.UTF8.GetBytes( "type=SNP#?version=1.0#?action=register#?app=KitKatTimer\n" );
                _socket.Send( registerMsg );

                _isRegistered = true;
            }
            catch
            {
                // Do nothing since we will try again soon
            }

            return;
        }

        // When user chooses Exit menu item
        void exitMenuHandler( object sender, EventArgs e )
        {
            // Unregister from Snarl

            try
            {
                byte[] unregisterMsg = Encoding.UTF8.GetBytes( "type=SNP#?version=1.0#?action=unregister#?app=KitKatTimer\n" );
                _socket.Send( unregisterMsg );
            }
            catch
            {
                // Do not care since we are exiting anyway
            }

            // Close this form

            Application.Exit();

            return;
        }

        void notifySnarl()
        {
            String notStr   = "type=SNP";
            notStr          += "#?version=1.0";
            notStr          += "#?action=notification";
            notStr          += "#?app=KitKatTimer";
            notStr          += "#?class=1";
            notStr          += "#?title=KitKatTimer: Take a break!";
            notStr          += ( 0 == ( _rand++ % 2 ) )
                            ? "#?text=You've been staring at this display for "
                            : "#?text=You've been sitting on your bum for ";
            notStr          += spanToString( _breakSpan );
            notStr          += ".#?timeout=5\n";
            byte[] notMsg   = Encoding.UTF8.GetBytes( notStr );

            try
            {
                _socket.Send( notMsg );     // Notify Snarl
                _lastTime = DateTime.Now;   // Restart time
            }
            catch
            {
                _isRegistered = false; // Reset status, so we try to connect soon
            }

            return;
        }

        void aboutHandler( object sender, EventArgs e )
        {
            MessageBox.Show(
                "\"Have a break ... have a Kit Kat!\"\nKitKatTimer asks you to take a break when you have been working too long.",
                "KitKatTimer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information );

            return;
        }

        String spanToString( TimeSpan span )
        {
            Int32 cmpVal = span.CompareTo( TimeSpan.FromHours( 1 ) );
            String timeStr;

            if ( cmpVal < 0 )
            {
                timeStr = span.Minutes + " minutes";
            }
            else if ( 0 == cmpVal )
            {
                timeStr = span.Hours + " hour";
            }
            else
            {
                timeStr = span.Hours + " hours";
            }

            return timeStr;
        }

        static void Main( string[] args )
        {
            Application.Run( new KitKatTimerApp() );
            return;
        }
    }
}

///////////////////////////////////////////////////////////////////////////////