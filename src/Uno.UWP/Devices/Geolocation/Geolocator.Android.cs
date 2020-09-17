#if __ANDROID__
#pragma warning disable 67
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Locations;
using Android.OS;
using Android.Runtime;
using Uno.Extensions;
using Windows.Extensions;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.UI.Core;

namespace Windows.Devices.Geolocation
{
	public sealed partial class Geolocator : Java.Lang.Object, ILocationListener
	{
		//using ConcurrentDictionary as concurrent HashSet (https://stackoverflow.com/questions/18922985/concurrent-hashsett-in-net-framework), byte is throwaway
		private static ConcurrentDictionary<Geolocator, byte> _positionChangedSubscriptions = new ConcurrentDictionary<Geolocator, byte>();

		private LocationManager _locationManager;
		private string _locationProvider;

		public Geolocator()
		{
		}

		private void TryInitialize()
		{
			if (_locationManager == null)
			{
				_locationManager = InitializeLocationProvider(1);
				_locationManager.RequestLocationUpdates(_locationProvider, 0, 0, this);
			}
		}

		public Task<Geoposition> GetGeopositionAsync()
		{
			if (CoreDispatcher.Main.HasThreadAccess)
			{
				TryInitialize();

				BroadcastStatus(PositionStatus.Initializing);
				var location = _locationManager.GetLastKnownLocation(_locationProvider);
				BroadcastStatus(PositionStatus.Ready);
				return Task.FromResult(location.ToGeoPosition());
			}
			else
			{
				return CoreDispatcher.Main.RunWithResultAsync(
					priority: CoreDispatcherPriority.Normal,
					task: () => GetGeopositionAsync()
				);
			}
		}

		public Task<Geoposition> GetGeopositionAsync(TimeSpan maximumAge, TimeSpan timeout)
			=> GetGeopositionAsync();

		public static async Task<GeolocationAccessStatus> RequestAccessAsync()
		{
			var status = GeolocationAccessStatus.Allowed;

			if (!await PermissionsHelper.CheckFineLocationPermission(CancellationToken.None))
			{
				status = await PermissionsHelper.TryGetFineLocationPermission(CancellationToken.None)
					? GeolocationAccessStatus.Allowed
					: GeolocationAccessStatus.Denied;

				BroadcastStatus(PositionStatus.Initializing);
				if (status == GeolocationAccessStatus.Allowed)
				{
					BroadcastStatus(PositionStatus.Ready);

					// If geolocators subscribed to PositionChanged before the location permission was granted,
					// make sure to initialize these geolocators now so they can start broadcasting.
					foreach(var subscriber in _positionChangedSubscriptions)
					{
						subscriber.Key.TryInitialize();
					}
				}
				else
				{
					BroadcastStatus(PositionStatus.Disabled);
				}
			}

			return status;
		}

		private LocationManager InitializeLocationProvider(double desiredAccuracy)
		{
			var locationManager = (LocationManager)global::Android.App.Application.Context.GetSystemService(Android.Content.Context.LocationService);

			var criteriaForLocationService = new Criteria
			{
				Accuracy = Accuracy.Coarse
			};

			var acceptableLocationProviders = locationManager.GetProviders(criteriaForLocationService, true);

			if (acceptableLocationProviders.Any())
			{
				_locationProvider = acceptableLocationProviders.First();
			}
			else
			{
				_locationProvider = String.Empty;
			}

			return locationManager;
		}

		partial void StartPositionChanged()
		{
			_positionChangedSubscriptions.TryAdd(this, 0);
		}

		partial void StopPositionChanged()
		{
			_positionChangedSubscriptions.TryRemove(this, out var _);
		}

		public void OnLocationChanged(Location location)
		{
			BroadcastStatus(PositionStatus.Ready);
			this._positionChanged?.Invoke(this, new PositionChangedEventArgs(location.ToGeoPosition()));
		}

		public void OnProviderDisabled(string provider)
		{
		}

		public void OnProviderEnabled(string provider)
		{
		}

		public void OnStatusChanged(string provider, [GeneratedEnum] Availability status, Bundle extras)
		{
		}		
	}

	static class Extensions
	{
		private const uint Wgs84SpatialReferenceId = 4326;

		public static Windows.Devices.Geolocation.Geoposition ToGeoPosition(this Location location)
		{
			double? geoheading = null;
			if (location.HasBearing)
			{
				geoheading = location.Bearing;
			}

			Windows.Devices.Geolocation.PositionSource posSource;
			switch (location.Provider)
			{
				case Android.Locations.LocationManager.NetworkProvider:
					posSource = Windows.Devices.Geolocation.PositionSource.Cellular;    // cell, wifi
					break;
				case Android.Locations.LocationManager.PassiveProvider:
					posSource = Windows.Devices.Geolocation.PositionSource.Unknown;  // other apps
					break;
				case Android.Locations.LocationManager.GpsProvider:
					posSource = Windows.Devices.Geolocation.PositionSource.Satellite;
					break;
				default:
					// ex.: "fused" - all merged, also e.g. Google Play
					posSource = Windows.Devices.Geolocation.PositionSource.Unknown;
					break;
			}

			Windows.Devices.Geolocation.BasicGeoposition basicGeoposition;
			basicGeoposition.Altitude = location.Altitude;
			basicGeoposition.Latitude = location.Latitude;
			basicGeoposition.Longitude = location.Longitude;

			Windows.Devices.Geolocation.Geopoint geopoint = new Windows.Devices.Geolocation.Geopoint(basicGeoposition,
						Windows.Devices.Geolocation.AltitudeReferenceSystem.Ellipsoid,
						Wgs84SpatialReferenceId
					);

			double? locVertAccuracy = null;
			// VerticalAccuracy is since API 26
			if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
			{
				if (location.HasVerticalAccuracy)
				{
					locVertAccuracy = location.VerticalAccuracyMeters;
				}
			}


			Windows.Devices.Geolocation.Geoposition geopos = new Windows.Devices.Geolocation.Geoposition(
				new Windows.Devices.Geolocation.Geocoordinate(
					latitude: location.Latitude,
					longitude: location.Longitude,
					altitude: location.Altitude,
					timestamp: DateTimeOffset.FromUnixTimeMilliseconds(location.Time),
					speed: location.HasSpeed ? location.Speed : 0,
					point: geopoint,
					accuracy: location.HasAccuracy ? location.Accuracy : 0,
					altitudeAccuracy: locVertAccuracy,
					heading: geoheading,
					positionSource: posSource
				)
			);

			return geopos;
		}


		private static DateTimeOffset FromUnixTime(long time)
		{
			var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			return epoch.AddMilliseconds(time);
		}
	}
}
#endif
