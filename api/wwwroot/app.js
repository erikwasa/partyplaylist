const state = {
  roomId: '',
  pollHandle: undefined
};

const els = {
  body: document.body,
  createRoom: document.querySelector('#create-room'),
  roomCard: document.querySelector('#room-card'),
  roomId: document.querySelector('#room-id'),
  guestLink: document.querySelector('#guest-link'),
  copyLink: document.querySelector('#copy-link'),
  deviceSelect: document.querySelector('#device-select'),
  refreshDevices: document.querySelector('#refresh-devices'),
  saveDevice: document.querySelector('#save-device'),
  requestedBy: document.querySelector('#requested-by'),
  roomInput: document.querySelector('#room-input'),
  searchForm: document.querySelector('#search-form'),
  searchQuery: document.querySelector('#search-query'),
  searchResults: document.querySelector('#search-results'),
  refreshQueue: document.querySelector('#refresh-queue'),
  queueNext: document.querySelector('#queue-next'),
  queueList: document.querySelector('#queue-list'),
  status: document.querySelector('#status')
};

init();

function init() {
  const params = new URLSearchParams(window.location.search);
  const roomFromUrl = params.get('roomId') || '';
  const storedRoomId = localStorage.getItem('partyplaylist.roomId') || '';
  const storedName = localStorage.getItem('partyplaylist.requestedBy') || '';
  const storedDeviceId = localStorage.getItem('partyplaylist.activeDeviceId') || '';

  if (roomFromUrl) {
    els.body.classList.add('guest-mode');
  }

  els.requestedBy.value = storedName;
  els.deviceSelect.value = storedDeviceId;
  setRoom(roomFromUrl || storedRoomId, false);

  els.createRoom.addEventListener('click', createRoom);
  els.copyLink.addEventListener('click', copyGuestLink);
  els.refreshDevices.addEventListener('click', loadDevices);
  els.saveDevice.addEventListener('click', saveDeviceForRoom);
  els.deviceSelect.addEventListener('change', () => {
    localStorage.setItem('partyplaylist.activeDeviceId', els.deviceSelect.value);
  });
  els.roomInput.addEventListener('change', () => setRoom(els.roomInput.value.trim()));
  els.requestedBy.addEventListener('change', () => {
    localStorage.setItem('partyplaylist.requestedBy', els.requestedBy.value.trim());
  });
  els.searchForm.addEventListener('submit', searchTracks);
  els.refreshQueue.addEventListener('click', refreshQueue);
  els.queueNext.addEventListener('click', queueNext);

  if (!els.body.classList.contains('guest-mode')) {
    loadDevices();
  }

  if (state.roomId) {
    refreshQueue();
    startPolling();
  } else {
    renderQueue([]);
  }
}

async function createRoom() {
  try {
    setStatus('Creating room...');
    const room = await api('/rooms', {
      method: 'POST',
      body: { activeDeviceId: els.deviceSelect.value || null }
    });
    setRoom(room.roomId);
    if (room.activeDeviceId) {
      els.deviceSelect.value = room.activeDeviceId;
      localStorage.setItem('partyplaylist.activeDeviceId', room.activeDeviceId);
    }
    setStatus('Room created. Share the guest link.');
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function loadDevices() {
  try {
    setStatus('Loading Spotify devices...');
    const selectedDeviceId = els.deviceSelect.value || localStorage.getItem('partyplaylist.activeDeviceId') || '';
    const devices = await api('/devices');
    renderDevices(devices, selectedDeviceId);
    setStatus(devices.length ? 'Devices loaded. Choose the host playback device.' : 'No Spotify devices found. Open Spotify and start playback on the host device.');
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function saveDeviceForRoom() {
  const roomId = getCurrentRoomId();
  if (!roomId) {
    setStatus('Create or enter a room before saving a device.', true);
    return;
  }

  try {
    const activeDeviceId = els.deviceSelect.value || null;
    const room = await api('/rooms/' + encodeURIComponent(roomId) + '/device', {
      method: 'POST',
      body: { activeDeviceId }
    });
    if (room.activeDeviceId) {
      localStorage.setItem('partyplaylist.activeDeviceId', room.activeDeviceId);
    } else {
      localStorage.removeItem('partyplaylist.activeDeviceId');
    }
    setStatus(room.activeDeviceId ? 'Playback device saved for this room.' : 'Room will use the current active Spotify device.');
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function searchTracks(event) {
  event.preventDefault();

  const query = els.searchQuery.value.trim();
  if (!query) {
    setStatus('Enter a search term first.', true);
    return;
  }

  try {
    setStatus('Searching Spotify...');
    els.searchResults.replaceChildren();
    const results = await api('/search?query=' + encodeURIComponent(query));
    renderSearchResults(results);
    setStatus(results.length ? 'Found ' + results.length + ' tracks.' : 'No tracks found.');
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function addTrack(track) {
  const roomId = getCurrentRoomId();
  const requestedBy = els.requestedBy.value.trim();

  if (!roomId) {
    setStatus('Enter or create a room before adding songs.', true);
    return;
  }

  if (!requestedBy) {
    setStatus('Enter your name or alias before adding songs.', true);
    els.requestedBy.focus();
    return;
  }

  try {
    setStatus('Adding ' + track.trackName + '...');
    localStorage.setItem('partyplaylist.requestedBy', requestedBy);
    await api('/rooms/' + encodeURIComponent(roomId) + '/queue', {
      method: 'POST',
      body: {
        trackUri: track.trackUri,
        trackName: track.trackName,
        artistName: track.artistName,
        requestedBy: requestedBy
      }
    });

    await refreshQueue();
    setStatus(track.trackName + ' was added to the queue.');
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function refreshQueue() {
  const roomId = getCurrentRoomId();
  if (!roomId) {
    renderQueue([]);
    setStatus('Create or enter a room to view its queue.');
    return;
  }

  try {
    const queue = await api('/rooms/' + encodeURIComponent(roomId) + '/queue');
    renderQueue(queue);
  } catch (error) {
    setStatus(error.message, true);
  }
}

async function queueNext() {
  const roomId = getCurrentRoomId();
  if (!roomId) {
    setStatus('Create or enter a room before queueing tracks.', true);
    return;
  }

  try {
    setStatus('Sending next waiting track to Spotify...');
    const result = await api('/rooms/' + encodeURIComponent(roomId) + '/queue-next', { method: 'POST', body: {} });
    await refreshQueue();
    setStatus(result.queued ? 'Next track sent to Spotify.' : result.message);
  } catch (error) {
    setStatus(error.message, true);
  }
}

function renderDevices(devices, selectedDeviceId) {
  els.deviceSelect.replaceChildren();

  const defaultOption = document.createElement('option');
  defaultOption.value = '';
  defaultOption.textContent = 'Use current active device';
  els.deviceSelect.append(defaultOption);

  for (const device of devices) {
    const option = document.createElement('option');
    option.value = device.id || '';
    option.disabled = !device.id || device.isRestricted;
    option.textContent = device.name + ' · ' + device.type
      + (device.isActive ? ' · active' : '')
      + (device.isRestricted ? ' · restricted' : '');
    els.deviceSelect.append(option);
  }

  if (selectedDeviceId && [...els.deviceSelect.options].some(option => option.value === selectedDeviceId)) {
    els.deviceSelect.value = selectedDeviceId;
  }
}

function renderSearchResults(results) {
  els.searchResults.replaceChildren();

  if (!results.length) {
    els.searchResults.append(empty('No search results yet.'));
    return;
  }

  for (const track of results) {
    const card = document.createElement('article');
    card.className = 'track-card';

    const image = document.createElement('img');
    image.alt = '';
    image.src = track.albumImageUrl || '';

    const details = document.createElement('div');
    const title = document.createElement('strong');
    title.textContent = track.trackName;
    const artist = document.createElement('span');
    artist.textContent = track.artistName;
    const duration = document.createElement('small');
    duration.textContent = formatDuration(track.durationMs);
    details.append(title, artist, duration);

    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = 'Add';
    button.addEventListener('click', () => addTrack(track));

    card.append(image, details, button);
    els.searchResults.append(card);
  }
}

function renderQueue(queue) {
  els.queueList.replaceChildren();

  if (!queue.length) {
    els.queueList.append(empty('The queue is empty.'));
    return;
  }

  for (const item of queue) {
    const row = document.createElement('li');
    row.className = 'queue-item status-' + item.status;

    const details = document.createElement('div');
    const title = document.createElement('strong');
    title.textContent = item.trackName;
    const meta = document.createElement('span');
    meta.textContent = item.artistName + ' · requested by ' + item.requestedBy;
    details.append(title, meta);

    const status = document.createElement('span');
    status.className = 'pill';
    status.textContent = item.status;

    row.append(details, status);
    els.queueList.append(row);
  }
}

function setRoom(roomId, persist = true) {
  state.roomId = roomId;
  els.roomInput.value = roomId;

  if (persist) {
    if (roomId) {
      localStorage.setItem('partyplaylist.roomId', roomId);
    } else {
      localStorage.removeItem('partyplaylist.roomId');
    }
  }

  if (roomId) {
    els.roomCard.classList.remove('is-hidden');
    els.roomId.textContent = roomId;
    els.guestLink.value = window.location.origin + '/api/app?roomId=' + encodeURIComponent(roomId);
    startPolling();
    refreshQueue();
  } else {
    els.roomCard.classList.add('is-hidden');
    stopPolling();
  }
}

function getCurrentRoomId() {
  const roomId = els.roomInput.value.trim();
  if (roomId !== state.roomId) {
    setRoom(roomId);
  }

  return state.roomId;
}

async function copyGuestLink() {
  try {
    await navigator.clipboard.writeText(els.guestLink.value);
    setStatus('Guest link copied.');
  } catch {
    els.guestLink.select();
    setStatus('Copy the selected guest link.');
  }
}

function startPolling() {
  stopPolling();
  state.pollHandle = window.setInterval(refreshQueue, 4000);
}

function stopPolling() {
  if (state.pollHandle) {
    window.clearInterval(state.pollHandle);
    state.pollHandle = undefined;
  }
}

async function api(path, options = {}) {
  const request = {
    method: options.method || 'GET',
    headers: {}
  };

  if (options.body !== undefined) {
    request.headers['Content-Type'] = 'application/json';
    request.body = JSON.stringify(options.body);
  }

  const response = await fetch('/api' + path, request);
  const contentType = response.headers.get('content-type') || '';
  const body = contentType.includes('application/json') ? await response.json() : await response.text();

  if (!response.ok) {
    const message = typeof body === 'object' && body
      ? [body.error, body.hint].filter(Boolean).join(' ')
      : 'Request failed with HTTP ' + response.status + '.';
    throw new Error(message || 'Request failed with HTTP ' + response.status + '.');
  }

  return body;
}

function setStatus(message, isError = false) {
  els.status.textContent = message;
  els.status.classList.toggle('is-error', isError);
}

function empty(message) {
  const item = document.createElement('li');
  item.className = 'empty';
  item.textContent = message;
  return item;
}

function formatDuration(durationMs) {
  const totalSeconds = Math.round(durationMs / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = String(totalSeconds % 60).padStart(2, '0');
  return minutes + ':' + seconds;
}
