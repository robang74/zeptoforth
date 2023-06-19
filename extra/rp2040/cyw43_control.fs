\ Copyright (c) 2023 Travis Bemann
\
\ Permission is hereby granted, free of charge, to any person obtaining a copy
\ of this software and associated documentation files (the "Software"), to deal
\ in the Software without restriction, including without limitation the rights
\ to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
\ copies of the Software, and to permit persons to whom the Software is
\ furnished to do so, subject to the following conditions:
\ 
\ The above copyright notice and this permission notice shall be included in
\ all copies or substantial portions of the Software.
\ 
\ THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
\ IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
\ FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
\ AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
\ LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
\ OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
\ SOFTWARE.

begin-module cyw43-control

  oo import
  lock import
  cyw43-structs import
  cyw43-consts import
  cyw43-runner import
  
  \ Unexpected ioctl data length exception
  : x-unexpected-ioctl-resp-len ( -- ) ." unexpected ioctl response length" cr ;

  \ clmload fialed
  : x-clmload-failed ( -- ) ." clmload failed" cr ;

  \ Unexpected MAC address length
  : x-unexpected-mac-addr-len ( -- ) ." unexpected MAC address length" cr ;
  
  \ Download chunk size
  1024 constant cyw43-download-chunk-size

  \ Link state
  0 constant cyw43-link-down
  1 constant cyw43-link-up

  \ Scratchpad size
  cyw43-download-chunk-size 8 + download-header-size + cell align
  constant cyw43-scratch-size
  
  \ CYW43 control class
  <object> begin-class <cyw43-control>

    \ CYW43 runner
    <cyw43-runner> class-size member cyw43-runner
    
    \ CYW43 control lock
    lock-size member cyw43-lock
    
    \ CYW43 CLM firmware address
    cell member cyw43-clm-addr

    \ CYW43 CLM firmware size
    cell member cyw43-clm-bytes

    \ MAC address
    6 cell align member cyw43-mac-addr

    \ Link state
    cell member cyw43-link-state

    \ CYW43 scratch size
    cyw43-scratch-size member cyw43-scratch-buf

    \ Initialize the CYW43
    method init-cyw43 ( self -- )

    \ Set power management
    method cyw43-power-management! ( pm self -- )

    \ Join an open AP
    method join-cyw43-open ( ssid-addr ssid-bytes self -- status success? )

    \ Join a WPA2 AP
    method join-cyw43-wpa2
    ( ssid-addr ssid-bytes pass-addr pass-bytes self -- status success? )

    \ Wait for joining an AP
    method wait-for-cyw43-join ( ssid-info self -- )
    
    \ Load the CLM
    method load-cyw43-clm ( self -- )

    \ Execute an ioctl
    method ioctl-cyw43 ( kind cmd iface buf-addr buf-size -- resp-len )

    \ Set an ioctl to a 32-bit value
    method >ioctl-cyw43-32 ( val cmd iface self -- )

    \ Set a variable with an ioctl
    method >iovar-cyw43 ( buf-addr buf-size name-addr name-size self -- )

    \ Get a variable with an ioctl
    method iovar-cyw43>
    ( buf-addr buf-size name-addr name-size self -- resp-len )
    
    \ Set a 32-bit variable with an ioctl
    method >iovar-cyw43-32 ( val name-addr name-size self -- )

    \ Set a double 32-bit variable with an ioctl
    method >iovar-cyw43-32x2 ( val0 val1 name-addr name-size self -- )

    \ Get a 32-bit variable with an ioctl
    method iovar-cyw43-32> ( val name-addr name-size self -- resp-len )
    
  end-class

  \ Implement the CYW43 control class
  <cyw43-control> begin-implement

    \ Constructor
    :noname { self }
      ( clm-addr clm-bytes fw-addr fw-bytes pwr clk dio pio-addr sm pio )

      \ Initialize the superclass
      self <object>->new

      \ Instantiate the runner
      <cyw43-runner> self cyw43-runner init-object

      \ Set the locals
      self cyw43-clm-bytes !
      self cyw43-clm-addr !
      6 0 ?do 0 self cyw43-mac-addr i + c! loop
      cyw43-link-down self cyw43-link-state !

      \ Initialize our lock
      self cyw43-lock init-lock

    ; define new

    \ Initialize the CYW43
    :noname { self -- }

      \ Get the CYW43 runner up and, well, running
      self cyw43-runner init-cyw43-runner
      self cyw43-runner run-cyw43

      \ Load the CLM
      self load-cyw43-clm

      cr ." Configuring misc stuff..."

      \ Disable tx gloming which transfers multiple packets in one request.
      0 s" bus:txglom" self >iovar-cyw43-32
      1 s" apsta" self >iovar-cyw43-32

      cr ." Getting MAC address..."
      
      self cyw43-mac-addr 6 s" cur_etheraddr" self iovar-cyw43>
      6 = averts x-unexpected-mac-addr-len
      cr ." MAC address: "
      6 0 ?do self cyw43-mac-addr i + c@ h.2 i 5 <> if ." :" then loop

      cr ." Setting country info..."

      self country-info-size [: { self buf }
        buf ci-country-abbrev [char] X over c! 1+ [char] X over c!
        1+ 0 over c! 1+ 0 swap c!
        buf ci-country-code [char] X over c! 1+ [char] X over c!
        1+ 0 over c! 1+ 0 swap c!
        -1 buf ci-rev !
        buf country-info-size s" country" self >iovar-cyw43
      ;] with-aligned-allot

      \ set country takes some time, next ioctls fail if we don't wait
      100 ms

      \ set antenna to chip antenna
      cr ." Set antenna to chip antenna"
      
      0 IOCTL_CMD_ANTDIV 0 self >ioctl-cyw43-32

      0 s" buf:txglom" self >iovar-cyw43-32
      100 ms
      8 s" ampdu_ba_wsize" self >iovar-cyw43-32
      100 ms
      4 s" ampdu_mpdu" self >iovar-cyw43-32
      100 ms

      \ Disable spammy uninteresting events
      self <cyw43-event-mask> [: { self events }
        cr ." Disable spammy uninteresting events"
        EVENT_RADIO events disable-cyw43-event
        EVENT_IF events disable-cyw43-event
        EVENT_PROBREQ_MSG events disable-cyw43-event
        EVENT_PROBREQ_MSG_RX events disable-cyw43-event
        EVENT_PROBRESP_MSG events disable-cyw43-event
        EVENT_PROBRESP_MSG_RX events disable-cyw43-event \ ???
        EVENT_ROAM events disable-cyw43-event
        events cyw43-event-mask event-mask-size s" bsscfg:event_msgs"
        self >iovar-cyw43
        100 ms
      ;] with-object

      \ set wifi up
      cr ." Set wifi up"
      0 0 0 0 cyw43-set IOCTL_CMD_UP 0 self ioctl-cyw43
      100 ms

      1 110 0 self >ioctl-cyw43-32 \ SET_GMODE = auto
      0 142 0 self >ioctl-cyw43-32 \ SET_BAND = any
      100 ms

    ; define init-cyw43

    \ Set power management
    :noname { pm self -- }
      pm cyw43-pm-mode 2 = if
        pm cyw43-pm-sleep-rest-ms s" pm2_sleep_rest" self >iovar-cyw43-32
        pm cyw43-pm-beacon-period s" bcn_li_bcn" self >iovar-cyw43-32
        pm cyw43-pm-dtim-period s" bcn_li_dtim" self >iovar-cyw43-32
        pm cyw43-pm-assoc s" assoc_listen" self >iovar-cyw43-32
      then
      pm cyw43-pm-mode 86 0 self >ioctl-cyw43-32
    ; define cyw43-power-management!
    
    \ Join an open AP
    :noname ( ssid-addr ssid-bytes self -- status success? )
      ssid-info-size [: { ssid-addr ssid-bytes self ssid-info }
        
        ssid-bytes 32 min 0 max to ssid-bytes
        
        8 s" ampdu_ba_wsize" self >iovar-cyw43-32
        
        0 134 0 self >ioctl-cyw43-32 \ wsec = open
        0 0 s" bsscfg:sup_wpa" self >iovar-cyw43-32x2
        1 20 0 self >ioctl-cyw43-32 \ set_infra = 1
        0 22 0 self >ioctl-cyw43-32 \ set_auth = open (0)

        ssid-bytes ssid-info si-len !
        ssid-addr ssid-info si-ssid ssid-bytes move

        ssid-info self wait-for-cyw43-join
        
      ;] with-aligned-allot
    ; define join-cyw43-open

    \ Join a WPA2 AP
    :noname
      ( ssid-addr ssid-bytes pass-addr pass-bytes self -- status success? )
      ssid-info-size [:
        passphrase-info-size [:
          { ssid-addr ssid-bytes pass-addr pass-bytes self ssid-info pass-info }

          ssid-bytes 32 min 0 max to ssid-bytes
          pass-bytes 64 min 0 max to pass-bytes
          
          8 s" ampdu_ba_wsize" self >iovar-cyw43-32
          
          4 134 0 self >ioctl-cyw43-32 \ wsec = wpa2
          1 0 s" bsscfg:sup_wpa" self >iovar-cyw43-32x2
          0 $FFFFFFFF s" bsscfg:sup_wpa2_eapver" self >iovar-cyw43-32x2
          0 2500 s" bsscfg:sup_wpa_tmo" self >iovar-cyw43-32x2

          100 ms

          pass-bytes pass-info pi-len h!
          1 pass-info pi-flags h!
          pass-addr pass-info pi-passphrase pass-bytes move

          pass-info passphrase-info-size 0 0
          cyw43-set IOCTL_CMD_SET_PASSPHRASE 0 self ioctl-cyw43 drop

          1 20 0 self >ioctl-cyw43-32 \ set_infra = 1
          0 22 0 self >ioctl-cyw43-32 \ set_auth = open (0)
          $80 165 0 self >ioctl-cyw43-32 \ set_wpa_auth

          ssid-bytes ssid-info si-len !
          ssid-addr ssid-info si-ssid ssid-bytes move
          
          ssid-info self wait-for-cyw43-join

        ;] with-aligned-allot
      ;] with-aligned-allot
    ; define join-cyw43-wpa2

    \ Wait for joining an AP
    :noname ( ssid-info self -- status success? )
      
      event-message-size [: { ssid-info self event }
        
        \ Enable events before joining so we don't lose any
        EVENT_SET_SSID self cyw43-runner enable-cyw43-event
        EVENT_AUTH self cyw43-runner enable-cyw43-event
        
        \ Set ssid
        ssid-info ssid-info-size 0 0 cyw43-set IOCTL_CMD_SET_SSID 0
        self ioctl-cyw43 drop
        
        0 0 { auth-status status }

        begin
          event self cyw43-runner get-cyw43-event
          event emsg-event-type @ EVENT_AUTH =
          event emsg-status @ ESTATUS_SUCCESS = and if
            event emsg-status @ to auth-status
            false
          else
            event emsg-event-type @ EVENT_SET_SSID = if
              event emsg_status @ to status
              true
            else
              false
            then
          then
        until
        
        self cyw43-runner disable-all-cyw43-events
        self cyw43-runner clear-cyw43-events

        status ESTATUS_SUCCESS = if
          cyw43-link-up self cyw43-link-state !
          cr ." JOINED"
          status true
        else
          cyw43-link-down self cyw43-link-state !
          cr ." JOIN failed with status=" status . ."  auth=" auth-status .
          status false
        then

      ;] with-aligned-allot
      
    ; define wait-for-cyw43-join

    \ Load the CLM
    :noname { self -- }
      
      self cyw43-clm-addr @ { addr }
      self cyw43-clm-size @ { bytes }
      
      begin bytes 0> while

        cr ." Downloading CLM..."
        
        \ Get the size of an actual chunk being sent
        bytes self cyw43-download-chunk-size min { len }

        \ Set the variable to be set
        s\" clmload\x00" self cyw43-scratch-buf 8 move

        \ Populate the download header
        cyw43-scratch-buf 8 + { dh-header }
        DOWNLOAD_FLAG_HANDLER_VER
        addr self cyw43-clm-addr @ = if DOWNLOAD_FLAG_BEGIN or then
        len bytes = if DOWNLOAD_FLAG_END or then
        dh-header dh-flag h!
        DOWNLOAD_TYPE_CLM dh-header dh-dload-type h!
        len dh-header dh-len !
        0 dh-header dh-crc !
        addr dh-header download-header-size + len move

        \ Download the chunk
        cyw43-scratch-buf bytes [ 8 download-header-size + ] literal + 0 0
        cyw43-set IOCTL_CMD_SET_VAR 0 self ioctl-cyw43 drop
        
        len +to addr
        len negate +to bytes
        
      repeat

      \ Checking that clmload is okay
      s" clmload_status" self iovar-cyw43-32> 0= averts x-clmload-failed
      
    ; define load-cyw43-clm

    \ Execute an ioctl
    :noname ( tx-addr tx-bytes rx-addr rx-bytes kind cmd iface -- resp-len )
      self cyw43-runner block-cyw43-ioctl
    ; define ioctl-cyw43

    \ Set an ioctl to a 32-bit value
    :noname { W^ val cmd iface -- }
      val cell 0 0 cyw43-set cmd iface self ioctl-cyw43 drop
    ; define >ioctl-cyw43-32
    
    \ Set a variable with an ioctl
    :noname { buf-addr buf-size name-addr name-size self -- }
      name-addr name-size buf-addr buf-size self
      name-size buf-size + 1+ 64 max [:
        { buf-addr buf-size name-addr name-size self ioctl-buf }
        name-addr ioctl-buf name-size move
        0 ioctl-buf name-size + c!
        buf-addr ioctl-buf name-size + 1+ buf-size move
        ioctl-buf name-size buf-size + 1+ 0 0 cyw43-set IOCTL_CMD_SET_VAR 0
        self ioctl-cyw43 drop
      ;] with-aligned-allot
    ; define >iovar-cyw43

    \ Get a variable with an ioctl
    :noname { buf-addr buf-size name-addr name-size self -- resp-len }
      name-addr name-size buf-addr buf-size self
      name-size 1+ buf-size max 64 max [:
        { buf-addr buf-size name-addr name-size self ioctl-buf }
        name-addr ioctl-buf name-size move
        0 ioctl-buf name-size + c!
        ioctl-buf name-size 1+ ioctl-buf buf-size 64 max
        cyw43-get IOCTL_CMD_GET_VAR 0 self ioctl-cyw43 { resp-len }
        ioctl-buf buf-addr resp-len move
        resp-len
      ;] with-aligned-allot
    ; define iovar-cyw43>

    \ Set a 32-bit variable with an ioctl
    :noname { W^ val name-addr name-size self -- }
      val cell name-addr name-size self >iovar-cyw43
    ; define >iovar-cyw43-32

    \ Set a double 32-bit variable with an ioctl
    :noname { val0 val1 name-addr name-size self -- }
      0 0 { D^ val } val0 val ! val1 val cell+ !
      val 2 cells name-addr name-size self >iovar-cyw43
    ; define >iovar-cyw34-32x2

    \ Get a 32-bit variable with an ioctl
    :noname { W^ val name-addr name-size self -- }
      val cell name-addr name-size self iovar-cyw43>
      cell = averts x-unexpected-ioctl-resp-len
    ; define iovar-cyw43-32>

  end-implement
  
end-module