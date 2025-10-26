//
//  RyujinxBridge.swift
//  MeloNX
//
//  Created by Stossy11 on 30/09/2025.
//

import Foundation

@_silgen_name("get_game_info")
func get_game_info(_ arg0: Int32, _ arg1: UnsafeMutablePointer<CChar>!) -> GameInfo

@_silgen_name("get_dlc_nca_list")
func get_dlc_nca_list(_ titleIdPtr: UnsafePointer<CChar>!, _ pathPtr: UnsafePointer<CChar>!) -> DlcNcaList

@_silgen_name("install_firmware")
func install_firmware(_ inputPtr: UnsafePointer<CChar>!)

@_silgen_name("installed_firmware_version")
func installed_firmware_version() -> UnsafeMutablePointer<CChar>!

@_silgen_name("set_native_window")
func set_native_window(_ layerPtr: UnsafeMutableRawPointer!)

@_silgen_name("pause_emulation")
func pause_emulation(_ shouldPause: Bool)

@_silgen_name("stop_emulation")
func stop_emulation()

@_silgen_name("initialize")
func initialize()

@_silgen_name("main_ryujinx_sdl")
func main_ryujinx_sdl(_ argc: Int32, _ argv: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>!) -> Int32

@_silgen_name("update_settings_external")
func update_settings_external(_ argc: Int32, _ argv: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>!) -> Int32

@_silgen_name("get_current_fps")
func get_current_fps() -> Int32

@_silgen_name("touch_began")
func touch_began(_ x: Float, _ y: Float, _ index: Int32)

@_silgen_name("touch_moved")
func touch_moved(_ x: Float, _ y: Float, _ index: Int32)

@_silgen_name("touch_ended")
func touch_ended(_ index: Int32)

@_silgen_name("refresh_account_manager")
func refresh_account_manager()

@_silgen_name("create_account")
func create_account(_ name: UnsafeMutablePointer<CChar>!, _ image: UnsafeMutablePointer<CChar>!, _ imagelength: Int32)

@_silgen_name("open_user")
func open_user(_ userid: UnsafeMutablePointer<CChar>!)

@_silgen_name("close_user")
func close_user(_ userid: UnsafeMutablePointer<CChar>!)

@_silgen_name("get_avatars")
func get_avatars() -> AvatarArray
